// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Candidate 2461 spent an hour in CI before its first scanner step was refused because the runner
/// still reported the scanner manifest of the previous backend. The launch preflight now walks the
/// candidate chain down to the pipelines whose scanners need that manifest and refuses the launch
/// while no eligible runner reports it, naming both hashes and the state of the agent update.
/// </summary>
public sealed class PipelineScannerManifestPreflightTests
{
    private static readonly string StaleManifest = new('7', 64);

    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineVariableResolver _variables = Substitute.For<IPipelineVariableResolver>();
    private readonly IPipelineTemplateResolver _templates = Substitute.For<IPipelineTemplateResolver>();
    private readonly IPipelineEnvironmentCheckGuard _checks = Substitute.For<IPipelineEnvironmentCheckGuard>();

    public PipelineScannerManifestPreflightTests()
    {
        _checks.CheckEnvironmentChecksAsync(Arg.Any<PipelineStageDefinition>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([1]);
        _variables.ResolveVariablesWithWarningsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(),
                Arg.Any<int?>(), Arg.Any<bool>())
            .Returns(_ => Task.FromResult((
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new List<string>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))));
        var guard = Substitute.For<IPipelinePortRegistryGuard>();
        guard.FindPortConflictsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(PortConflictReport.Empty);
        _portGuard = guard;
        ChildExists("aetheus-quality", QualityWithScanner());
    }

    private readonly IPipelinePortRegistryGuard _portGuard;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PipelineRunPreflightService BuildSut()
    {
        var dispatch = new PipelineDispatchServerResolver(_repo);
        return new PipelineRunPreflightService(
            _repo,
            dispatch,
            _checks,
            _portGuard,
            new PipelineChildPipelineResolver(_templates, _variables),
            Substitute.For<IPipelineRequirementsChecker>(),
            new PipelineScannerManifestPreflight(_repo, dispatch),
            new PipelineReleaseArtifactPreflight(Substitute.For<Aetheus.Back.Components.Artifacts.IArtifactRepository>(), Substitute.For<Aetheus.Back.Components.Artifacts.IArtifactStorageService>()),
            new PipelineAiProfilePreflight(Substitute.For<Aetheus.Back.Components.AiTasks.IAiTaskService>()),
            Substitute.For<ILogger<PipelineRunPreflightService>>());
    }

    private static PipelineYamlDefinition Orchestrator(string name) => new()
    {
        Name = name,
        Stages =
        [
            new PipelineStageDefinition
            {
                Name = "Quality",
                Os = "linux",
                Steps = [new PipelineStepDefinition { Name = "quality", Type = "trigger", Pipeline = "aetheus-quality" }]
            }
        ]
    };

    private static PipelineYamlDefinition QualityWithScanner() => new()
    {
        Name = "aetheus-quality",
        Stages =
        [
            new PipelineStageDefinition
            {
                Name = "Lint and duplication",
                Os = "linux",
                Steps = [new PipelineStepDefinition { Name = "Check duplication", Type = "scanner", Scanner = "jscpd" }]
            }
        ]
    };

    private void ChildExists(string name, PipelineYamlDefinition definition)
    {
        var pipeline = new Pipeline { Id = 7, ProjectId = 5, Name = name, YamlDefinition = $"name: {name}\nstages: []" };
        _repo.FindPipelineByNameAndProjectAsync(name, 5, Arg.Any<CancellationToken>()).Returns(pipeline);
        _templates.ResolveAsync(pipeline.YamlDefinition, Arg.Any<int>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateResolution(definition, pipeline.YamlDefinition, null, null, null, false));
    }

    /// <summary>One configured runner able to take pipeline work, reporting the given manifest.</summary>
    private void RunnerReports(string? manifest)
        => _repo.GetRunnerFactsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new PipelineRunnerFacts(
                    1, "vps2577917", ServerStatus.Online, AgentProtocol.CurrentVersion,
                    [AgentCapabilities.PipelineBuild],
                    manifest is null ? null : $"[\"scanner-manifest:sha256:{manifest}\"]")
            ]);

    private async Task<PipelinePreflightOutcome> LaunchAsync(string rootName) =>
        await BuildSut().FindBlockingProblemsAsync(
            Orchestrator(rootName),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            organizationId: 3,
            projectId: 5,
            Ct);

    [Fact]
    public async Task Candidate_WithNoRunnerOnTheEmbeddedManifest_IsRefusedAtLaunch_NamingBothHashesAndTheExplicitUpdate()
    {
        RunnerReports(StaleManifest);

        var outcome = await LaunchAsync("aetheus-candidate");

        var found = Assert.Single(outcome.Problems);
        Assert.Equal(PipelineProblemCodes.ScannerManifestNotReported, found.Code);
        Assert.Equal(ScannerManifestCatalog.Sha256, found.Parameters["expected"]);
        Assert.Equal("'vps2577917'", found.Parameters["runners"]);
        var problem = found.English;
        Assert.Contains("aetheus-quality", problem, StringComparison.Ordinal);
        Assert.Contains("Lint and duplication", problem, StringComparison.Ordinal);
        Assert.Contains(ScannerManifestCatalog.Sha256, problem, StringComparison.Ordinal);
        Assert.Contains(StaleManifest, problem, StringComparison.Ordinal);
        Assert.Contains("vps2577917", problem, StringComparison.Ordinal);
        Assert.Contains("Update the agent from its server page", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("automatic", problem, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(outcome.Checks, check => check.Kind == "scanner-manifest" && !check.Satisfied);
    }

    [Fact]
    public async Task Candidate_WhoseRunnerReportsNoManifest_IsRefused_SayingSo()
    {
        RunnerReports(null);

        var problem = Assert.Single((await LaunchAsync("aetheus-candidate")).Problems).English;

        Assert.Contains("'vps2577917' reports no manifest", problem, StringComparison.Ordinal);
        Assert.Contains(ScannerManifestCatalog.Sha256, problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Candidate_WithAnAlignedRunner_IsAccepted()
    {
        RunnerReports(ScannerManifestCatalog.Sha256);

        var outcome = await LaunchAsync("aetheus-candidate");

        Assert.Empty(outcome.Problems);
    }

    [Fact]
    public async Task ThePipeline_LaunchedOutsideTheCandidate_IsNotHeldToTheManifest()
    {
        // Dispatch enforces the manifest only for UPSTREAM_PIPELINE=aetheus-candidate; the preflight
        // must not refuse what dispatch would accept.
        RunnerReports(StaleManifest);

        var outcome = await LaunchAsync("aetheus-nightly");

        Assert.Empty(outcome.Problems);
        Assert.DoesNotContain(outcome.Checks, check => check.Kind == "scanner-manifest");
    }

    /// <summary>A project's candidate created by the setup wizard is a candidate because it extends
    /// application-candidate, not because of its name.</summary>
    [Fact]
    public async Task AnyProjectsCandidate_ExtendingTheCandidateTemplate_IsHeldToTheManifest()
    {
        RunnerReports(StaleManifest);
        _repo.FindPipelineByNameAndProjectAsync("portfolio-candidate", 5, Arg.Any<CancellationToken>())
            .Returns(new Pipeline
            {
                Id = 9,
                ProjectId = 5,
                Name = "portfolio-candidate",
                TemplateReferenceName = "application-candidate",
                TemplateReferenceVersion = 2
            });

        var problem = Assert.Single((await LaunchAsync("portfolio-candidate")).Problems).English;

        Assert.Contains(ScannerManifestCatalog.Sha256, problem, StringComparison.Ordinal);
        Assert.Contains(StaleManifest, problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyProjectsPipeline_ExtendingAnotherTemplate_IsNotHeldToTheManifest()
    {
        RunnerReports(StaleManifest);
        _repo.FindPipelineByNameAndProjectAsync("portfolio-nightly", 5, Arg.Any<CancellationToken>())
            .Returns(new Pipeline
            {
                Id = 9,
                ProjectId = 5,
                Name = "portfolio-nightly",
                TemplateReferenceName = "application-nightly",
                TemplateReferenceVersion = 2
            });

        Assert.Empty((await LaunchAsync("portfolio-nightly")).Problems);
    }
}
