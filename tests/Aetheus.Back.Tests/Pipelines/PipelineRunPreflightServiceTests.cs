// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The launch-time preflight: what a run needs is asked for before anything is dispatched.
///
/// The distinction every case here turns on is "unmatchable" versus "not right now". A selector no
/// server is configured for can never resolve itself and must refuse the launch; a configured server
/// that happens to be offline is the scheduler's stand-by path and must NOT become a launch error,
/// or a transient outage would read as a broken pipeline.
/// </summary>
public sealed class PipelineRunPreflightServiceTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineRequirementsChecker _requirements = Substitute.For<IPipelineRequirementsChecker>();

    private PipelineRunPreflightService BuildSut(IPipelineEnvironmentCheckGuard? checks = null)
        => new(
            _repo,
            new PipelineDispatchServerResolver(_repo),
            checks ?? Admits(true),
            SilentPortGuard(),
            new PipelineChildPipelineResolver(
                Substitute.For<IPipelineTemplateResolver>(), Substitute.For<IPipelineVariableResolver>()),
            _requirements,
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
        string name, string? agent = null, string? environment = null, params PipelineStepDefinition[] steps)
        => new()
        {
            Name = name,
            // Agent is a non-nullable string on the DTO; "no selector" is the empty string, not null.
            Agent = agent ?? string.Empty,
            Environment = environment,
            Steps = steps.Length > 0 ? [.. steps] : [new() { Name = "run", Shell = "make" }]
        };

    private async Task<IReadOnlyList<string>> RunAsync(PipelineYamlDefinition definition, int? projectId = 7)
        => (await BuildSut().FindBlockingProblemsAsync(
            definition, new Dictionary<string, string>(), organizationId: 3, projectId, CancellationToken.None)).Problems;

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
    public async Task SelectorNoServerIsConfiguredFor_RefusesTheLaunchAndNamesTheSelector()
    {
        ConfiguredTargets();

        var problems = await RunAsync(Definition(Stage("QA", agent: "ghost-agent")));

        var problem = Assert.Single(problems);
        Assert.Contains("QA", problem, StringComparison.Ordinal);
        Assert.Contains("ghost-agent", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredButOfflineServer_IsNotALaunchError()
    {
        // A server exists for the selector; the fleet query says nothing about it being online, and
        // the preflight must not invent that verdict. The scheduler parks the run instead.
        ConfiguredTargets(42);

        var problems = await RunAsync(Definition(Stage("QA", agent: "linux-01")));

        Assert.Empty(problems);
    }

    [Fact]
    public async Task StageWithNoSelectorAtAll_IsLeftToDispatch()
    {
        // No pool, environment or agent: the stage falls back to any runner of the organization, a
        // case the candidate-target query cannot express. Refusing here would block ordinary runs.
        ConfiguredTargets();

        var problems = await RunAsync(Definition(Stage("build")));

        Assert.Empty(problems);
    }

    [Fact]
    public async Task EnvironmentCheckThatRefuses_IsReportedAtLaunchInsteadOfHoursIn()
    {
        // An invalid QA environment must refuse launch before earlier stages consume resources.
        ConfiguredTargets(42);
        var sut = BuildSut(Admits(false));

        var outcome = await sut.FindBlockingProblemsAsync(
            Definition(Stage("QA", agent: "linux-01", environment: "qa")),
            new Dictionary<string, string>(), organizationId: 3, projectId: 7, CancellationToken.None);

        var problem = Assert.Single(outcome.Problems);
        Assert.Contains("qa", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TriggerStepNamingAPipelineThatDoesNotExist_RefusesTheLaunch()
    {
        ConfiguredTargets(42);
        _repo.FindPipelineByNameAndProjectAsync("aetheus-qa", 7, Arg.Any<CancellationToken>())
            .Returns((Pipeline?)null);

        var problems = await RunAsync(Definition(Stage(
            "QA", agent: "linux-01", environment: null,
            new PipelineStepDefinition { Name = "run QA", Type = "trigger", Pipeline = "aetheus-qa" })));

        var problem = Assert.Single(problems);
        Assert.Contains("aetheus-qa", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtifactSourcePipelineThatExists_PassesPreflight()
    {
        ConfiguredTargets(42);
        _repo.FindPipelineByNameAndProjectAsync("aetheus-ci", 7, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "aetheus-ci" });

        var problems = await RunAsync(Definition(Stage(
            "Restore", agent: "linux-01", environment: null,
            new PipelineStepDefinition
            {
                Name = "restore",
                Type = "restore-artifacts",
                ArtifactSourcePipeline = "aetheus-ci"
            })));

        Assert.Empty(problems);
    }

    private Task<PipelinePreflightOutcomeDto> RunWithVariablesAsync(
        PipelineYamlDefinition definition, params (string Key, string Value)[] variables)
        => BuildSut().FindBlockingProblemsAsync(
            definition,
            variables.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase),
            organizationId: 3, projectId: 7, CancellationToken.None);

    private static PipelineStageDefinition CandidateStage(
        string artifactSourcePipeline, Dictionary<string, string>? stageVariables = null) => new()
        {
            Name = "Candidate",
            Agent = "linux-01",
            Variables = stageVariables ?? [],
            Steps =
        [
            new PipelineStepDefinition
            {
                Name = "Publish immutable undeployed candidate", Type = "release",
                ArtifactSourcePipeline = artifactSourcePipeline
            }
        ]
        };

    /// <summary>Preflight resolves variable-based child names just as dispatch does.</summary>
    [Fact]
    public async Task TemplateVariableNamingTheArtifactSource_IsJudgedOnTheExpandedName()
    {
        ConfiguredTargets(42);
        _repo.FindPipelineByNameAndProjectAsync("aetheus-ci", 7, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "aetheus-ci" });

        var outcome = await RunWithVariablesAsync(
            Definition(CandidateStage("$(APPLICATION_CI_PIPELINE)")),
            ("APPLICATION_CI_PIPELINE", "aetheus-ci"));

        Assert.Empty(outcome.Problems);
        Assert.Contains(outcome.Checks, check =>
            check.Kind == "pipeline-reference" && check.Subject == "aetheus-ci" && check.Satisfied);
    }

    [Fact]
    public async Task TemplateVariableExpandingToAMissingPipeline_IsRefusedNamingTheExpandedName()
    {
        ConfiguredTargets(42);

        var outcome = await RunWithVariablesAsync(
            Definition(CandidateStage("$(APPLICATION_CI_PIPELINE)")),
            ("APPLICATION_CI_PIPELINE", "aetheus-cii"));

        var problem = Assert.Single(outcome.Problems);
        Assert.Contains("names pipeline 'aetheus-cii', which does not exist", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("$(", problem, StringComparison.Ordinal);
    }

    /// <summary>Non-regression, not the bug: a literal name involves no expansion, and a typo in it
    /// must stay a refused launch.</summary>
    [Fact]
    public async Task LiteralPipelineNameWithATypo_IsStillRefused()
    {
        ConfiguredTargets(42);
        _repo.FindPipelineByNameAndProjectAsync("aetheus-ci", 7, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "aetheus-ci" });

        var outcome = await RunWithVariablesAsync(
            Definition(CandidateStage("aetheus-cii")),
            ("APPLICATION_CI_PIPELINE", "aetheus-ci"));

        var problem = Assert.Single(outcome.Problems);
        Assert.Contains("names pipeline 'aetheus-cii', which does not exist", problem, StringComparison.Ordinal);
    }

    /// <summary>The stage's own `variables:` override the run's at dispatch, so they do here too.</summary>
    [Fact]
    public async Task StageVariableOverridingTheRunValue_IsTheNameJudged()
    {
        ConfiguredTargets(42);
        _repo.FindPipelineByNameAndProjectAsync("aetheus-ci", 7, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "aetheus-ci" });

        var outcome = await RunWithVariablesAsync(
            Definition(CandidateStage(
                "$(APPLICATION_CI_PIPELINE)",
                new Dictionary<string, string> { ["APPLICATION_CI_PIPELINE"] = "aetheus-ci" })),
            ("APPLICATION_CI_PIPELINE", "application-ci"));

        Assert.Empty(outcome.Problems);
    }

    /// <summary>
    /// A name a previous step publishes has no value at launch. It is not "a pipeline that does not
    /// exist": the launch goes on, with a warning saying the check happens when the step runs. Whether
    /// the name can be provided at all is the unresolved-variable guard's refusal, taken earlier.
    /// </summary>
    [Fact]
    public async Task NameOnlyKnownFromAStepOutput_IsNotedInsteadOfRefused()
    {
        ConfiguredTargets(42);
        var definition = Definition(
            new PipelineStageDefinition
            {
                Name = "Pick",
                Agent = "linux-01",
                Steps = [new PipelineStepDefinition { Name = "pick", Shell = "sh pick.sh", Outputs = ["SOURCE_PIPELINE"] }]
            },
            CandidateStage("$(SOURCE_PIPELINE)"));

        var outcome = await RunWithVariablesAsync(definition);

        Assert.Empty(outcome.Problems);
        var warning = Assert.Single(outcome.Warnings);
        Assert.Contains("$(SOURCE_PIPELINE)", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(outcome.Checks, check => check.Kind == "pipeline-reference");
        await _repo.DidNotReceive().FindPipelineByNameAndProjectAsync(
            Arg.Is<string>(name => name.Contains("$(")), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A default must not stand in for a value the run has yet to produce: at dispatch the
    /// output may name another pipeline. Without an output to wait for, the default is the name.</summary>
    [Fact]
    public async Task DefaultOnANameStillToCome_IsNotTakenAtLaunch()
    {
        ConfiguredTargets(42);
        var pick = new PipelineStageDefinition
        {
            Name = "Pick",
            Agent = "linux-01",
            Steps = [new PipelineStepDefinition { Name = "pick", Shell = "sh pick.sh", Outputs = ["SOURCE_PIPELINE"] }]
        };

        var waiting = await RunWithVariablesAsync(Definition(pick, CandidateStage("$(SOURCE_PIPELINE:-aetheus-cii)")));
        var defaulted = await RunWithVariablesAsync(Definition(CandidateStage("$(SOURCE_PIPELINE:-aetheus-cii)")));

        Assert.Empty(waiting.Problems);
        Assert.Single(waiting.Warnings);
        var problem = Assert.Single(defaulted.Problems);
        Assert.Contains("names pipeline 'aetheus-cii'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryUnmetRequirementIsReportedTogether_NotJustTheFirst()
    {
        // One launch, one answer: fixing them one run at a time is the slow loop this replaces.
        ConfiguredTargets();

        var problems = await RunAsync(Definition(
            Stage("QA", agent: "ghost-a"),
            Stage("Deploy", agent: "ghost-b")));

        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public async Task WithoutAProject_PipelineReferencesAreLeftAlone()
    {
        // A pipeline with no project cannot resolve a name to a pipeline, so the check is skipped
        // rather than guessed. Silently refusing would block every project-less pipeline.
        ConfiguredTargets(42);

        var problems = await RunAsync(
            Definition(Stage(
                "QA", agent: "linux-01", environment: null,
                new PipelineStepDefinition { Name = "run QA", Type = "trigger", Pipeline = "aetheus-qa" })),
            projectId: null);

        Assert.Empty(problems);
    }

    [Fact]
    public async Task EnvironmentEntityIsNeverRequiredToExistForAStageWithoutOne()
    {
        ConfiguredTargets(42);
        _repo.FindEnvironmentByNameAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((Environment?)null);

        var problems = await RunAsync(Definition(Stage("build", agent: "linux-01")));

        Assert.Empty(problems);
    }

    /// <summary>R3-67: a declared requirement written relative to the project is checked under the
    /// project's own name, the one the setup wizard created.</summary>
    [Fact]
    public async Task DeclaredRequirements_AreCheckedUnderTheProjectPrefixedNames()
    {
        ConfiguredTargets(11);
        _repo.GetProjectNameAsync(7, Arg.Any<CancellationToken>()).Returns("Mon Projet");
        var definition = Definition(Stage("build")) with
        {
            Requires = new PipelineRequiresDefinition
            {
                Libraries = ["${{ project.slug }}.share"],
                Vaults = ["${{project.slug}}.qa"]
            }
        };

        await RunAsync(definition);

        await _requirements.Received(1).CheckAsync(
            Arg.Is<PipelineRequiresDefinition?>(requires => requires != null
                && requires.Libraries.SequenceEqual(new[] { "mon-projet.share" })
                && requires.Vaults.SequenceEqual(new[] { "mon-projet.qa" })),
            7, 3, Arg.Any<CancellationToken>());
    }
}
