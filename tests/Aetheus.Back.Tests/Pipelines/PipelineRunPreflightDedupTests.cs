// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The preflight runs inside the synchronous launch request, so work it repeats per stage is work the
/// user waits for. Three findings of the 2026-08-20 audit were the same mistake in three places: the
/// fleet query, the environment gate (a DNS lookup plus an HTTP GET per required check) and the
/// referenced-pipeline lookup were all issued once per stage instead of once per distinct key.
///
/// These count calls rather than assert timings: the defect is the call count, and a timing assertion
/// would be flaky without proving anything more.
/// </summary>
public sealed class PipelineRunPreflightDedupTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();

    private PipelineRunPreflightService BuildSut(IPipelineEnvironmentCheckGuard checks)
        => new(_repo, new PipelineDispatchServerResolver(_repo), checks,
            SilentPortGuard(),
            new PipelineChildPipelineResolver(
                Substitute.For<IPipelineTemplateResolver>(), Substitute.For<IPipelineVariableResolver>()),
            Substitute.For<IPipelineRequirementsChecker>(),
            new PipelineScannerManifestPreflight(_repo, new PipelineDispatchServerResolver(_repo)),
            new PipelineReleaseArtifactPreflight(Substitute.For<Aetheus.Back.Components.Artifacts.IArtifactRepository>(), Substitute.For<Aetheus.Back.Components.Artifacts.IArtifactStorageService>()),
            new PipelineAiProfilePreflight(Substitute.For<Aetheus.Back.Components.AiTasks.IAiTaskService>()),
            Substitute.For<ILogger<PipelineRunPreflightService>>());

    private static IPipelineEnvironmentCheckGuard Admits(bool verdict)
    {
        var guard = Substitute.For<IPipelineEnvironmentCheckGuard>();
        guard.CheckEnvironmentChecksAsync(Arg.Any<PipelineStageDefinition>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(verdict);
        return guard;
    }

    private void ConfiguredTargets(params int[] ids)
        => _repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(ids.ToList());

    private static PipelineYamlDefinition Definition(params PipelineStageDefinition[] stages)
        => new() { Name = "p", Stages = [.. stages] };

    private static PipelineStageDefinition Stage(
        string name, string agent, string? environment = null, params PipelineStepDefinition[] steps)
        => new()
        {
            Name = name,
            Agent = agent,
            Environment = environment,
            Steps = steps.Length > 0 ? [.. steps] : [new() { Name = "run", Shell = "make" }]
        };

    private async Task<IReadOnlyList<string>> RunAsync(
        PipelineYamlDefinition definition, IPipelineEnvironmentCheckGuard? checks = null)
        => (await BuildSut(checks ?? Admits(true)).FindBlockingProblemsAsync(
            definition, new Dictionary<string, string>(), organizationId: 3, projectId: 7,
            CancellationToken.None)).Problems.Select(problem => problem.English).ToList();

    /// <summary>A registry with nothing to say: these tests are about the OTHER preflight checks, and an
    /// unconfigured substitute would return a null report instead of an empty one.</summary>
    private static IPipelinePortRegistryGuard SilentPortGuard()
    {
        var guard = Substitute.For<IPipelinePortRegistryGuard>();
        guard.FindPortConflictsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(PortConflictReport.Empty);
        return guard;
    }

    [Fact]
    public async Task StagesSharingOneSelector_QueryTheFleetOnce_NotOncePerStage()
    {
        ConfiguredTargets(11);

        var problems = await RunAsync(Definition(
            Stage("CI", "linux"), Stage("Quality", "linux"),
            Stage("Security", "linux"), Stage("QA", "linux")));

        Assert.Empty(problems);
        await _repo.Received(1).FindCandidateTargetServerIdsAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
            Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StagesWithDifferentSelectors_StillQueryThefleetPerDistinctSelector()
    {
        // The memoization keys on the selector; collapsing everything into one answer would make the
        // guard blind to a stage nobody is configured for.
        ConfiguredTargets(11);

        await RunAsync(Definition(Stage("CI", "linux"), Stage("QA", "windows")));

        await _repo.Received(2).FindCandidateTargetServerIdsAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
            Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StagesSharingOneEnvironment_EvaluateItsChecksOnce_NotOncePerStage()
    {
        ConfiguredTargets(11);
        var guard = Admits(true);

        await RunAsync(
            Definition(
                Stage("Deploy", "linux", "prod"),
                Stage("Smoke", "linux", "prod"),
                Stage("Verify", "linux", "prod")),
            guard);

        await guard.Received(1).CheckEnvironmentChecksAsync(
            Arg.Any<PipelineStageDefinition>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoDistinctEnvironments_AreBothEvaluated()
    {
        ConfiguredTargets(11);
        var guard = Admits(true);

        await RunAsync(
            Definition(Stage("Deploy", "linux", "staging"), Stage("Promote", "linux", "prod")),
            guard);

        await guard.Received(2).CheckEnvironmentChecksAsync(
            Arg.Any<PipelineStageDefinition>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARefusingEnvironment_StillNamesEveryStageThatTargetsIt()
    {
        // Deduplicating the evaluation must not cost a diagnostic: the operator needs to know which
        // stages are blocked, not just that the environment refuses.
        ConfiguredTargets(11);

        var problems = await RunAsync(
            Definition(Stage("Deploy", "linux", "prod"), Stage("Smoke", "linux", "prod")),
            Admits(false));

        var problem = Assert.Single(problems);
        Assert.Contains("Deploy", problem, StringComparison.Ordinal);
        Assert.Contains("Smoke", problem, StringComparison.Ordinal);
        Assert.Contains("prod", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameReferencedPipeline_IsResolvedOnce_ButReportedForEveryStageThatNamesIt()
    {
        ConfiguredTargets(11);
        _repo.FindPipelineByNameAndProjectAsync("missing-pipeline", 7, Arg.Any<CancellationToken>())
            .Returns((Pipeline?)null);

        var problems = await RunAsync(Definition(
            Stage("A", "linux", null, new PipelineStepDefinition { Name = "t1", Pipeline = "missing-pipeline" }),
            Stage("B", "linux", null, new PipelineStepDefinition { Name = "t2", Pipeline = "missing-pipeline" })));

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, problem => problem.Contains("'A'", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("'B'", StringComparison.Ordinal));
        await _repo.Received(1).FindPipelineByNameAndProjectAsync(
            "missing-pipeline", 7, Arg.Any<CancellationToken>());
    }
}
