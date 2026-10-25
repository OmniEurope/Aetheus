// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using static Aetheus.Back.Components.Pipelines.PipelineReleaseGateVariables;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-24: a deployment run receives the Gate's verdict on the release its <c>candidateVersion</c> names, read
/// from the release of the run's own project, so the generic deployment template asks for an extra approval for a
/// rejected or F release, and for any release it cannot vouch for. The release lookup is the real EF query
/// (<see cref="PipelineServerResolver"/>) over an in-memory database, so the project filter is the one production runs.
/// </summary>
public sealed class PipelineReleaseGateVariablesTests : IDisposable
{
    private const int ProjectId = 1;
    private const int OtherProjectId = 2;
    private const string ApprovalCondition = "ne(variables['RELEASE_GATE_APPROVAL'], 'not-required')";

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly PipelineVariableResolver _sut;
    private int _nextReleaseId = 1;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PipelineReleaseGateVariablesTests()
    {
        var releases = new PipelineServerResolver(_db);
        _repo.FindDeploymentGateReleaseAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => releases.FindDeploymentGateReleaseAsync(call.ArgAt<int>(0), call.ArgAt<string>(1), call.ArgAt<CancellationToken>(2)));
        _sut = new PipelineVariableResolver(
            Substitute.For<IVariableLibraryService>(), Substitute.For<IVaultService>(), _repo,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), TimeProvider.System);
    }

    private async Task AddReleaseAsync(int projectId, string version, bool? deployable, AnalysisGrade? grade)
    {
        _db.Releases.Add(new Release
        {
            Id = _nextReleaseId++,
            ProjectId = projectId,
            Version = version,
            Deployable = deployable,
            AssuranceGrade = grade,
            DetectedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)
        });
        await _db.SaveChangesAsync(Ct);
    }

    private static PipelineYamlDefinition DeployDefinition(Dictionary<string, string>? variables = null) => new()
    {
        Name = "deploy-prod",
        Trigger = "manual",
        Parameters = [new PipelineTemplateParameterDefinition { Name = CandidateVersionParameter, Required = true }],
        Variables = variables ?? []
    };

    /// <summary>A run as the launcher persists it: the effective parameter under its own name and under the
    /// <c>parameters.</c> namespace, plus whatever else the caller handed in.</summary>
    private static PipelineRun DeployRun(int? projectId, string version, Dictionary<string, string>? extra = null)
    {
        var variables = new Dictionary<string, string>(extra ?? [])
        {
            [CandidateVersionParameter] = version,
            [PipelineCommandBuilder.ParameterKeyPrefix + CandidateVersionParameter] = version
        };
        return new PipelineRun
        {
            Id = 42,
            PipelineId = 7,
            AdditionalVariablesJson = JsonSerializer.Serialize(variables),
            Pipeline = new Pipeline { Id = 7, Name = "deploy-prod", ProjectId = projectId }
        };
    }

    private Task<Dictionary<string, string>> ResolveStageAsync(PipelineRun run, PipelineYamlDefinition? definition = null) =>
        _sut.ResolveFullVariablesForRunAsync(run, definition ?? DeployDefinition(), Ct);

    private static void AssertGate(Dictionary<string, string> vars, string status, string grade, string approval)
    {
        Assert.Equal(status, vars[StatusVariable]);
        Assert.Equal(grade, vars[GradeVariable]);
        Assert.Equal(approval, vars[ApprovalVariable]);
        // The template's own condition, read by the engine's evaluator: the approval stage runs exactly when required.
        var verdict = PipelineRunHelpers.EvaluateCondition(ApprovalCondition, vars, false);
        Assert.Null(verdict.Error);
        Assert.Equal(approval == ApprovalRequired, verdict.Met);
    }

    [Fact]
    public async Task ARejectedRelease_RequiresTheApproval()
    {
        await AddReleaseAsync(ProjectId, "1.4.0", deployable: false, AnalysisGrade.B);

        AssertGate(await ResolveStageAsync(DeployRun(ProjectId, "1.4.0")), Rejected, "B", ApprovalRequired);
    }

    [Fact]
    public async Task AnAcceptedReleaseGradedF_RequiresTheApproval()
    {
        await AddReleaseAsync(ProjectId, "1.4.0", deployable: true, AnalysisGrade.F);

        AssertGate(await ResolveStageAsync(DeployRun(ProjectId, "1.4.0")), Accepted, "F", ApprovalRequired);
    }

    [Theory]
    [InlineData(AnalysisGrade.B)]
    [InlineData(AnalysisGrade.E)]
    public async Task AnAcceptedReleaseGradedAToE_DeploysWithoutTheApproval(AnalysisGrade grade)
    {
        await AddReleaseAsync(ProjectId, "1.4.0", deployable: true, grade);

        AssertGate(await ResolveStageAsync(DeployRun(ProjectId, "1.4.0")), Accepted, grade.ToString(), ApprovalNotRequired);
    }

    [Fact]
    public async Task AnUnknownRelease_FailsClosed()
    {
        await AddReleaseAsync(ProjectId, "1.4.0", deployable: true, AnalysisGrade.A);

        AssertGate(await ResolveStageAsync(DeployRun(ProjectId, "9.9.9")), Unknown, Unknown, ApprovalRequired);
    }

    [Fact]
    public async Task AReleaseWithoutVerdictOrGrade_FailsClosed()
    {
        await AddReleaseAsync(ProjectId, "1.4.0", deployable: null, grade: null);

        AssertGate(await ResolveStageAsync(DeployRun(ProjectId, "1.4.0")), Unqualified, NoGrade, ApprovalRequired);
    }

    [Fact]
    public async Task ARunWithoutProject_FailsClosed()
    {
        await AddReleaseAsync(ProjectId, "1.4.0", deployable: true, AnalysisGrade.A);

        AssertGate(await ResolveStageAsync(DeployRun(projectId: null, "1.4.0")), Unknown, Unknown, ApprovalRequired);
        await _repo.DidNotReceive().FindDeploymentGateReleaseAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnotherProjectsRelease_IsNeverRead()
    {
        // Same version in two projects: the run of project 1 reads its own rejected release, never the accepted A
        // of project 2; a run of project 3, which has no such release, does not borrow project 2's either.
        await AddReleaseAsync(ProjectId, "2.0.0", deployable: false, AnalysisGrade.C);
        await AddReleaseAsync(OtherProjectId, "2.0.0", deployable: true, AnalysisGrade.A);

        AssertGate(await ResolveStageAsync(DeployRun(ProjectId, "2.0.0")), Rejected, "C", ApprovalRequired);
        AssertGate(await ResolveStageAsync(DeployRun(3, "2.0.0")), Unknown, Unknown, ApprovalRequired);
    }

    [Fact]
    public async Task NeitherThePipelineNorTheCaller_CanClaimAVerdict()
    {
        await AddReleaseAsync(ProjectId, "1.4.0", deployable: false, AnalysisGrade.F);
        var forged = new Dictionary<string, string>
        {
            [StatusVariable] = Accepted,
            [GradeVariable] = "A",
            [ApprovalVariable] = ApprovalNotRequired
        };

        var vars = await ResolveStageAsync(DeployRun(ProjectId, "1.4.0", extra: forged), DeployDefinition(forged));

        AssertGate(vars, Rejected, "F", ApprovalRequired);
    }

    [Fact]
    public async Task TheLaunchAndPreviewResolution_CarriesTheVerdictToo()
    {
        await AddReleaseAsync(ProjectId, "1.4.0", deployable: false, AnalysisGrade.D);
        var launchVariables = new Dictionary<string, string>
        {
            [PipelineCommandBuilder.ParameterKeyPrefix + CandidateVersionParameter] = "1.4.0"
        };

        var (vars, _, _) = await _sut.ResolveVariablesWithWarningsAsync(DeployDefinition(), ProjectId, launchVariables, Ct);

        AssertGate(vars, Rejected, "D", ApprovalRequired);
    }

    [Fact]
    public async Task ARunThatDeploysNoCandidate_GetsNoGateVariables()
    {
        var run = new PipelineRun
        {
            Id = 43,
            PipelineId = 8,
            AdditionalVariablesJson = "{}",
            Pipeline = new Pipeline { Id = 8, Name = "ci", ProjectId = ProjectId }
        };

        var vars = await _sut.ResolveFullVariablesForRunAsync(run, new PipelineYamlDefinition { Name = "ci", Trigger = "manual" }, Ct);

        Assert.DoesNotContain(StatusVariable, vars.Keys);
        Assert.DoesNotContain(GradeVariable, vars.Keys);
        Assert.DoesNotContain(ApprovalVariable, vars.Keys);
    }

    [Fact]
    public void TheTemplateCondition_RequiresTheApprovalWhenTheVariableIsAbsent()
    {
        // A backend that does not write RELEASE_GATE_APPROVAL must leave the approval in place, never skip it.
        var verdict = PipelineRunHelpers.EvaluateCondition(ApprovalCondition, [], false);
        Assert.Null(verdict.Error);
        Assert.True(verdict.Met);
    }

    [Fact]
    public void AParameter_CannotTakeAGateVariableName()
    {
        var errors = PipelineParameterResolver.ValidateDeclarations(
            [new PipelineTemplateParameterDefinition { Name = ApprovalVariable }]);

        Assert.Contains(errors, error => error.Contains("reserved system variable name", StringComparison.Ordinal));
    }

    public void Dispose() => _db.Dispose();
}
