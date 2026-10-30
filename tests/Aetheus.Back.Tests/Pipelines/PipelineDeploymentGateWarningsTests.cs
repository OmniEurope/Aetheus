// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-72: every reason the quality gate has against the release a deployment is launched with, as the launch
/// confirmation lists them and the run records them; nothing for an ordinary pipeline or a clean release.
/// </summary>
public sealed class PipelineDeploymentGateWarningsTests
{
    private const int ProjectId = 4;

    private static List<DeploymentGateWarningKind> Kinds(DeploymentGateRelease release, AnalysisGrade? minimum = null) =>
        [.. PipelineDeploymentGateWarnings.Collect(release, minimum).Select(warning => warning.Kind)];

    [Fact]
    public void ACleanRelease_HasNoWarning()
    {
        Assert.Empty(Kinds(new DeploymentGateRelease(1, "1.0.0", AnalysisGrade.B, Deployable: true, BlockingTestCount: 0), AnalysisGrade.E));
    }

    [Fact]
    public void AReleaseWithoutGrade_SaysSoOnce_NotAlsoThatItHasNoVerdict()
    {
        Assert.Equal([DeploymentGateWarningKind.NoGrade], Kinds(new DeploymentGateRelease(1, "1.0.0", null)));
    }

    [Fact]
    public void AGradeBelowTheThreshold_NamesTheGradeAndTheThreshold_InsteadOfSayingF()
    {
        var warning = Assert.Single(PipelineDeploymentGateWarnings.Collect(
            new DeploymentGateRelease(1, "1.0.0", AnalysisGrade.F, Deployable: true), AnalysisGrade.C));

        Assert.Equal(DeploymentGateWarningKind.BelowMinimum, warning.Kind);
        Assert.Equal(AnalysisGrade.F, warning.Grade);
        Assert.Equal(AnalysisGrade.C, warning.MinimumGrade);
    }

    [Fact]
    public void AnFWithinAnFThreshold_IsStillSaid()
    {
        Assert.Equal([DeploymentGateWarningKind.GradedF],
            Kinds(new DeploymentGateRelease(1, "1.0.0", AnalysisGrade.F, Deployable: true), AnalysisGrade.F));
    }

    [Fact]
    public void EveryReason_IsListed_InOrder_WithItsCounts()
    {
        var warnings = PipelineDeploymentGateWarnings.Collect(
            new DeploymentGateRelease(1, "1.0.0", AnalysisGrade.D, Deployable: false, BlockingTestCount: 3,
                KeptArtifactCount: 1, OpenSecurityFindingCount: 2), AnalysisGrade.C);

        Assert.Equal(
            [DeploymentGateWarningKind.BelowMinimum, DeploymentGateWarningKind.Rejected,
             DeploymentGateWarningKind.BlockingTests, DeploymentGateWarningKind.SecurityFindings],
            warnings.Select(warning => warning.Kind));
        Assert.Equal(3, warnings[2].Count);
        Assert.Equal(2, warnings[3].Count);
    }

    [Fact]
    public void AGradedReleaseWithoutVerdict_SaysTheGateGaveNone()
    {
        Assert.Equal([DeploymentGateWarningKind.NoVerdict], Kinds(new DeploymentGateRelease(1, "1.0.0", AnalysisGrade.A)));
    }

    [Fact]
    public async Task APipelineWithoutCandidateVersion_IsNotAssessed()
    {
        var repo = Substitute.For<IPipelineRepository>();
        var definition = new PipelineYamlDefinition { Name = "build" };

        var assessment = await PipelineDeploymentGateWarnings.AssessAsync(
            definition, Variables("1.0.0", "E"), ProjectId, repo, CancellationToken.None);

        Assert.Null(assessment);
        await repo.DidNotReceive().FindDeploymentGateReleaseAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADeployment_IsAssessedAgainstTheDeclaredThreshold_InItsOwnProject()
    {
        var repo = Substitute.For<IPipelineRepository>();
        repo.FindDeploymentGateReleaseAsync(ProjectId, "1.0.0", Arg.Any<CancellationToken>())
            .Returns(new DeploymentGateRelease(9, "1.0.0", AnalysisGrade.D, Deployable: true));

        var assessment = await PipelineDeploymentGateWarnings.AssessAsync(
            Deployment(), Variables("1.0.0", "C"), ProjectId, repo, CancellationToken.None);

        Assert.NotNull(assessment);
        Assert.Equal(9, assessment.Release!.Id);
        Assert.Equal(DeploymentGateWarningKind.BelowMinimum, Assert.Single(assessment.Warnings).Kind);
    }

    [Fact]
    public async Task AnUnknownRelease_IsAssessedWithoutWarnings_SoTheLaunchCanRefuseIt()
    {
        var repo = Substitute.For<IPipelineRepository>();

        var assessment = await PipelineDeploymentGateWarnings.AssessAsync(
            Deployment(), Variables("9.9.9", "E"), ProjectId, repo, CancellationToken.None);

        Assert.NotNull(assessment);
        Assert.Null(assessment.Release);
        Assert.Empty(assessment.Warnings);
    }

    private static PipelineYamlDefinition Deployment() => new()
    {
        Name = "deploy",
        Parameters = [new PipelineTemplateParameterDefinition { Name = "candidateVersion", Type = "string" }]
    };

    private static Dictionary<string, string> Variables(string version, string threshold) => new(StringComparer.OrdinalIgnoreCase)
    {
        [PipelineCommandBuilder.ParameterKeyPrefix + "candidateVersion"] = version,
        ["AETHEUS_DEPLOY_MINIMUM_GRADE"] = threshold
    };
}
