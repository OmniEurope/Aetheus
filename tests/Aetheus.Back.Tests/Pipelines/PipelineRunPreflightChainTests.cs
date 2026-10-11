// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The preflight follows the pipelines a run will trigger, not only the one being launched.
///
/// An orchestrator declares no environment and no variable library of its own: everything it needs
/// belongs to the pipelines it triggers and to the deployment it proposes. Its launch therefore
/// passed every check while the chain still died an hour in, on an environment whose required check
/// refuses, or two hours in at the deployment, on a library entry nobody had created.
///
/// The other half of the contract matters just as much: a false refusal here blocks every launch and
/// would be worse than the late failure it replaces, so anything a parent can legitimately supply at
/// run time must count as supplied.
/// </summary>
public sealed class PipelineRunPreflightChainTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineVariableResolver _variables = Substitute.For<IPipelineVariableResolver>();
    private readonly IPipelineTemplateResolver _templates = Substitute.For<IPipelineTemplateResolver>();
    private readonly IPipelineEnvironmentCheckGuard _checks = Substitute.For<IPipelineEnvironmentCheckGuard>();

    public PipelineRunPreflightChainTests()
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
            .Returns(call => Task.FromResult((
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new List<string>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))));
    }

    private PipelineRunPreflightService BuildSut() => new(
        _repo,
        new PipelineDispatchServerResolver(_repo),
        _checks,
        SilentPortGuard(),
        new PipelineChildPipelineResolver(_templates, _variables),
        Substitute.For<IPipelineRequirementsChecker>(),
        new PipelineScannerManifestPreflight(_repo, new PipelineDispatchServerResolver(_repo)),
        new PipelineReleaseArtifactPreflight(Substitute.For<Aetheus.Back.Components.Artifacts.IArtifactRepository>()),
        Substitute.For<ILogger<PipelineRunPreflightService>>());

    /// <summary>A registry with nothing to say: these tests are about the chain walk, and an
    /// unconfigured substitute returns a null report instead of an empty one (PLAN-005 made
    /// FindPortConflictsAsync return blocking conflicts and non-blocking observations).</summary>
    private static IPipelinePortRegistryGuard SilentPortGuard()
    {
        var guard = Substitute.For<IPipelinePortRegistryGuard>();
        guard.FindPortConflictsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(PortConflictReport.Empty);
        return guard;
    }

    private static PipelineYamlDefinition Orchestrator(string childName) => new()
    {
        Name = "candidate",
        Stages =
        [
            new PipelineStageDefinition
            {
                Name = "QA",
                Agent = string.Empty,
                Steps =
                [
                    new PipelineStepDefinition
                    {
                        Name = "run qa", Type = "trigger", Pipeline = childName,
                        Variables = new Dictionary<string, string> { ["AETHEUS_PROFILE"] = "light" }
                    }
                ]
            }
        ]
    };

    private static PipelineYamlDefinition Child(string? environment = null, string agent = "") => new()
    {
        Name = "child",
        Stages =
        [
            new PipelineStageDefinition
            {
                Name = "Validate",
                Agent = agent,
                Environment = environment,
                Steps = [new PipelineStepDefinition { Name = "test", Shell = "make test" }]
            }
        ]
    };

    private void ChildExists(string name, PipelineYamlDefinition definition)
    {
        var pipeline = new Pipeline { Id = 7, ProjectId = 5, Name = name, YamlDefinition = "name: child\nstages: []" };
        _repo.FindPipelineByNameAndProjectAsync(name, 5, Arg.Any<CancellationToken>()).Returns(pipeline);
        _templates.ResolveAsync(pipeline.YamlDefinition, Arg.Any<int>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateResolution(definition, pipeline.YamlDefinition, null, null, null, false));
    }

    private async Task<IReadOnlyList<string>> RunAsync(PipelineYamlDefinition definition) =>
        (await BuildSut().FindBlockingProblemsAsync(
            definition,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            organizationId: 3,
            projectId: 5,
            TestContext.Current.CancellationToken)).Problems;

    private async Task<IReadOnlyList<PreflightCheckDto>> RecordedChecksAsync(PipelineYamlDefinition definition) =>
        (await BuildSut().FindBlockingProblemsAsync(
            definition,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            organizationId: 3,
            projectId: 5,
            TestContext.Current.CancellationToken)).Checks;

    [Fact]
    public async Task AnEnvironmentThatRefusesInAChildRefusesTheParentLaunch()
    {
        ChildExists("aetheus-qa", Child(environment: "qa"));
        _checks.CheckEnvironmentChecksAsync(
                Arg.Is<PipelineStageDefinition>(stage => stage.Environment == "qa"), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var problems = await RunAsync(Orchestrator("aetheus-qa"));

        var problem = Assert.Single(problems);
        Assert.Contains("aetheus-qa", problem, StringComparison.Ordinal);
        Assert.Contains("environment 'qa'", problem, StringComparison.Ordinal);
    }

    /// <summary>PLAN-004 R-02: a template triggers "$(APPLICATION_QA_PIPELINE)". The chain followed is
    /// the pipeline the dispatcher will start, not a lookup of the raw text that finds nothing.</summary>
    [Fact]
    public async Task ATemplatedTriggerNameIsFollowedToThePipelineItExpandsTo()
    {
        ChildExists("aetheus-qa", Child(environment: "qa"));
        _checks.CheckEnvironmentChecksAsync(
                Arg.Is<PipelineStageDefinition>(stage => stage.Environment == "qa"), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var outcome = await BuildSut().FindBlockingProblemsAsync(
            Orchestrator("$(APPLICATION_QA_PIPELINE)"),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["APPLICATION_QA_PIPELINE"] = "aetheus-qa"
            },
            organizationId: 3,
            projectId: 5,
            TestContext.Current.CancellationToken);

        var problem = Assert.Single(outcome.Problems);
        Assert.StartsWith("aetheus-qa: ", problem, StringComparison.Ordinal);
        Assert.Contains("environment 'qa'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingVariableLibraryInAChildRefusesTheParentLaunch()
    {
        ChildExists("aetheus-deploy-prod", Child());
        _variables.ResolveVariablesWithWarningsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>(), "aetheus-deploy-prod",
                Arg.Any<int?>(), Arg.Any<bool>())
            .Returns<Task<(Dictionary<string, string>, List<string>, HashSet<string>)>>(
                _ => throw new BadRequestException(
                    "Pipeline variable(s) could not be resolved: 'PORT_FRONT_BLUE' (referenced by BG_PORTS)."));

        var problems = await RunAsync(Orchestrator("aetheus-deploy-prod"));

        var problem = Assert.Single(problems);
        Assert.Contains("aetheus-deploy-prod", problem, StringComparison.Ordinal);
        Assert.Contains("PORT_FRONT_BLUE", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOnSuccessTargetIsCheckedLikeATriggeredOne()
    {
        ChildExists("aetheus-deploy-prod", Child(environment: "prod"));
        _checks.CheckEnvironmentChecksAsync(
                Arg.Is<PipelineStageDefinition>(stage => stage.Environment == "prod"), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var definition = new PipelineYamlDefinition
        {
            Name = "candidate",
            OnSuccess = [new PipelineDownstreamTrigger { Pipeline = "aetheus-deploy-prod", Release = "latest" }],
            Stages =
            [
                new PipelineStageDefinition
                {
                    Name = "Build", Agent = string.Empty,
                    Steps = [new PipelineStepDefinition { Name = "build", Shell = "make" }]
                }
            ]
        };

        var problems = await RunAsync(definition);

        Assert.Contains(problems, problem => problem.Contains("aetheus-deploy-prod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOnSuccessTargetThatDoesNotExistIsNamed()
    {
        _repo.FindPipelineByNameAndProjectAsync("gone", 5, Arg.Any<CancellationToken>()).Returns((Pipeline?)null);
        var definition = new PipelineYamlDefinition
        {
            Name = "candidate",
            OnSuccess = [new PipelineDownstreamTrigger { Pipeline = "gone" }],
            Stages =
            [
                new PipelineStageDefinition
                {
                    Name = "Build", Agent = string.Empty,
                    Steps = [new PipelineStepDefinition { Name = "build", Shell = "make" }]
                }
            ]
        };

        var problems = await RunAsync(definition);

        var problem = Assert.Single(problems);
        Assert.Contains("on_success names pipeline 'gone'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHealthyChainIsNotRefused()
    {
        ChildExists("aetheus-qa", Child(environment: "qa"));

        var problems = await RunAsync(Orchestrator("aetheus-qa"));

        Assert.Empty(problems);
    }

    [Fact]
    public async Task EveryNameTheParentForwardsCountsAsProvidedToTheChild()
    {
        // The whole risk of this traversal is a false refusal, so what a trigger step forwards must
        // reach the child's resolution as a known name, whatever its value.
        ChildExists("aetheus-qa", Child());
        Dictionary<string, string>? seenSeed = null;
        _variables.ResolveVariablesWithWarningsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>(), "aetheus-qa",
                Arg.Any<int?>(), Arg.Any<bool>())
            .Returns(call =>
            {
                seenSeed = call.ArgAt<Dictionary<string, string>?>(2);
                return Task.FromResult((
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    new List<string>(),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            });

        await RunAsync(Orchestrator("aetheus-qa"));

        Assert.NotNull(seenSeed);
        Assert.True(seenSeed!.ContainsKey("AETHEUS_PROFILE"));
    }

    [Fact]
    public async Task AParentValueOnlyKnownOnceAParentStageRuns_ReachesTheChildAsAProvidedEmptyValue()
    {
        // aetheus-nightly: BG_BROWSER_IMAGE names an output of its own Prepare demo host. Handed to a
        // child verbatim, the child's guard saw a reference nothing in the CHILD provides and refused
        // all six children. The name is provided; only its value is unknown at launch.
        ChildExists("aetheus-qa", Child());
        Dictionary<string, string>? seenSeed = null;
        _variables.ResolveVariablesWithWarningsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>(), "aetheus-qa",
                Arg.Any<int?>(), Arg.Any<bool>())
            .Returns(call =>
            {
                seenSeed = call.ArgAt<Dictionary<string, string>?>(2);
                return Task.FromResult((
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    new List<string>(),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            });
        var parentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BG_BROWSER_IMAGE"] = "$(AETHEUS_BROWSER_SMOKE_IMAGE)",
            ["BG_PROJECT"] = "aetheus-demo",
            ["WITH_DEFAULT"] = "$(MISSING:-fallback)"
        };

        await BuildSut().FindBlockingProblemsAsync(
            Orchestrator("aetheus-qa"), parentVariables, organizationId: 3, projectId: 5,
            TestContext.Current.CancellationToken);

        Assert.NotNull(seenSeed);
        Assert.Equal(string.Empty, seenSeed!["BG_BROWSER_IMAGE"]);
        Assert.Equal("aetheus-demo", seenSeed["BG_PROJECT"]);
        Assert.Equal("$(MISSING:-fallback)", seenSeed["WITH_DEFAULT"]);
        Assert.False(seenSeed.ContainsKey("AETHEUS_BROWSER_SMOKE_IMAGE"));
    }

    [Fact]
    public async Task AChildIsResolvedWithTheReferencesItMustBeAbleToSatisfy()
    {
        // enforceResolvedReferences is what turns "this library entry does not exist" into a refusal
        // instead of a placeholder rendered into a deployed Apache vhost.
        ChildExists("aetheus-qa", Child());

        await RunAsync(Orchestrator("aetheus-qa"));

        await _variables.Received().ResolveVariablesWithWarningsAsync(
            Arg.Any<PipelineYamlDefinition>(), 5, Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>(), "aetheus-qa",
            Arg.Any<int?>(), true);
    }

    [Fact]
    public async Task ACycleBetweenPipelinesTerminates()
    {
        var loop = new PipelineYamlDefinition
        {
            Name = "child",
            Stages =
            [
                new PipelineStageDefinition
                {
                    Name = "Back", Agent = string.Empty,
                    Steps = [new PipelineStepDefinition { Name = "again", Type = "trigger", Pipeline = "candidate" }]
                }
            ]
        };
        ChildExists("aetheus-qa", loop);
        var parent = new Pipeline { Id = 1, ProjectId = 5, Name = "candidate", YamlDefinition = "name: candidate\nstages: []" };
        _repo.FindPipelineByNameAndProjectAsync("candidate", 5, Arg.Any<CancellationToken>()).Returns(parent);
        _templates.ResolveAsync(parent.YamlDefinition, Arg.Any<int>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateResolution(Orchestrator("aetheus-qa"), parent.YamlDefinition, null, null, null, false));

        var problems = await RunAsync(Orchestrator("aetheus-qa"));

        Assert.Empty(problems);
    }

    [Fact]
    public async Task AChildDefinitionThatCannotBeResolvedIsNamedRatherThanIgnored()
    {
        var pipeline = new Pipeline { Id = 7, ProjectId = 5, Name = "aetheus-qa", YamlDefinition = "name: child\nstages: []" };
        _repo.FindPipelineByNameAndProjectAsync("aetheus-qa", 5, Arg.Any<CancellationToken>()).Returns(pipeline);
        _templates.ResolveAsync(pipeline.YamlDefinition, Arg.Any<int>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<PipelineTemplateResolution>>(
                _ => throw new BadRequestException("Template 'application-candidate@9' was not found."));

        var problems = await RunAsync(Orchestrator("aetheus-qa"));

        var problem = Assert.Single(problems);
        Assert.Contains("aetheus-qa", problem, StringComparison.Ordinal);
        Assert.Contains("application-candidate@9", problem, StringComparison.Ordinal);
    }

    // --- Lot 12.4: what the preflight verified, kept so an accepted launch is not silent. ---

    [Fact]
    public async Task AnAcceptedLaunch_RecordsTheChildPipelineItWalked()
    {
        ChildExists("aetheus-qa", new PipelineYamlDefinition { Name = "qa", Stages = [] });

        var checks = await RecordedChecksAsync(Orchestrator("aetheus-qa"));

        Assert.Contains(checks, check =>
            check.Kind == "child-pipeline" && check.Subject == "aetheus-qa" && check.Satisfied);
        Assert.Contains(checks, check =>
            check.Kind == "pipeline-reference" && check.Subject == "aetheus-qa" && check.Satisfied);
    }

    [Fact]
    public async Task AMissingChildPipeline_IsRecordedAsAnUnsatisfiedReference()
    {
        // The refusal itself creates no run, so this record only exists on a run whose refusals were
        // tolerated; it still must not claim the reference was fine.
        _repo.FindPipelineByNameAndProjectAsync("aetheus-qa", 5, Arg.Any<CancellationToken>())
            .Returns((Pipeline?)null);

        var checks = await RecordedChecksAsync(Orchestrator("aetheus-qa"));

        var reference = Assert.Single(checks, check => check.Kind == "pipeline-reference");
        Assert.False(reference.Satisfied);
        Assert.Contains("does not exist", reference.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameRequirementNamedTwice_IsRecordedOnce()
    {
        // Two stages sharing one selector are one requirement, not two; the record should read as a
        // list of requirements rather than a list of stages.
        ChildExists("aetheus-qa", new PipelineYamlDefinition
        {
            Name = "qa",
            Stages =
            [
                new PipelineStageDefinition { Name = "A", Agent = "linux-01" },
                new PipelineStageDefinition { Name = "B", Agent = "linux-01" }
            ]
        });

        var checks = await RecordedChecksAsync(Orchestrator("aetheus-qa"));

        Assert.Single(checks, check => check.Kind == "runner" && check.Subject.Contains("linux-01", StringComparison.Ordinal));
    }
}
