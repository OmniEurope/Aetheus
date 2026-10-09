// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Environment preconditions of the candidate chain that the control plane already knows at launch,
/// and that used to surface only when the stage was reached: a configured runner no dispatch will ever
/// select (protocol window, capability), and a retained release artifact the QA rollback proof
/// restores through <c>release: previous-deployed</c>.
/// </summary>
public sealed class PipelineLaunchPreconditionTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IArtifactRepository _artifacts = Substitute.For<IArtifactRepository>();
    private readonly IPipelineVariableResolver _variables = Substitute.For<IPipelineVariableResolver>();
    private readonly IPipelineTemplateResolver _templates = Substitute.For<IPipelineTemplateResolver>();

    public PipelineLaunchPreconditionTests()
    {
        _repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([1]);
        Runner(AgentProtocol.CurrentVersion, AgentCapabilities.PipelineBuild);
        _variables.ResolveVariablesWithWarningsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(),
                Arg.Any<int?>(), Arg.Any<bool>())
            .Returns(call => Task.FromResult((
                new Dictionary<string, string>(
                    call.ArgAt<Dictionary<string, string>?>(2) ?? [], StringComparer.OrdinalIgnoreCase),
                new List<string>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PipelineRunPreflightService BuildSut(ILogger<PipelineRunPreflightService>? logger = null)
    {
        var checks = Substitute.For<IPipelineEnvironmentCheckGuard>();
        checks.CheckEnvironmentChecksAsync(Arg.Any<PipelineStageDefinition>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var ports = Substitute.For<IPipelinePortRegistryGuard>();
        ports.FindPortConflictsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(PortConflictReport.Empty);
        var dispatch = new PipelineDispatchServerResolver(_repo);
        return new PipelineRunPreflightService(
            _repo, dispatch, checks, ports,
            new PipelineChildPipelineResolver(_templates, _variables),
            Substitute.For<IPipelineRequirementsChecker>(),
            new PipelineScannerManifestPreflight(_repo, dispatch),
            new PipelineReleaseArtifactPreflight(_artifacts),
            logger ?? Substitute.For<ILogger<PipelineRunPreflightService>>());
    }

    private void Runner(int? protocol, params string[] capabilities)
        => _repo.GetRunnerFactsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([new PipelineRunnerFacts(1, "vps2577917", ServerStatus.Offline, protocol, capabilities, null)]);

    private static PipelineYamlDefinition Build() => new()
    {
        Name = "aetheus-ci",
        Stages =
        [
            new PipelineStageDefinition
            {
                Name = "Build", Os = "linux",
                Steps = [new PipelineStepDefinition { Name = "build", Shell = "make" }]
            }
        ]
    };

    private static PipelineYamlDefinition Qa(bool allowMissing = true) => new()
    {
        Name = "aetheus-qa",
        Stages =
        [
            new PipelineStageDefinition
            {
                Name = "Restore V-1 runtime", Os = "linux",
                Steps =
                [
                    new PipelineStepDefinition
                    {
                        Name = "Restore exact retained V-1 browser runtime", Type = "restore-artifacts",
                        Release = "previous-deployed", Artifact = "BrowserRuntime-artifacts",
                        TargetDirectory = ".nminus1", AllowMissing = allowMissing
                    }
                ]
            }
        ]
    };

    private static PipelineYamlDefinition Candidate(bool onSuccess = false) => new()
    {
        Name = "aetheus-candidate",
        Stages = onSuccess
            ? []
            :
            [
                new PipelineStageDefinition
                {
                    Name = "QA", Os = "linux",
                    Steps =
                    [
                        new PipelineStepDefinition
                        {
                            Name = "qa", Type = "trigger", Pipeline = "aetheus-qa", InheritSource = false,
                            SourceCommit = "$(BUILD_SOURCEVERSION)"
                        }
                    ]
                }
            ],
        OnSuccess = onSuccess ? [new PipelineDownstreamTrigger { Pipeline = "aetheus-qa" }] : []
    };

    private void ChildExists(PipelineYamlDefinition definition)
    {
        var pipeline = new Pipeline { Id = 7, ProjectId = 5, Name = definition.Name!, YamlDefinition = "stages: []" };
        _repo.FindPipelineByNameAndProjectAsync(definition.Name!, 5, Arg.Any<CancellationToken>()).Returns(pipeline);
        _templates.ResolveAsync(pipeline.YamlDefinition, Arg.Any<int>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateResolution(definition, pipeline.YamlDefinition, null, null, null, false));
    }

    private void RetainedV1(bool browserRuntimePresent, bool requiredByContract)
    {
        _artifacts.FindPreviousDeployedReleaseArtifactAsync(5, Commit, "BrowserRuntime-artifacts", Arg.Any<CancellationToken>())
            .Returns(browserRuntimePresent
                ? new PipelineArtifact { Id = 9, Name = "BrowserRuntime-artifacts", Sha256 = new string('c', 64) }
                : null);
        _artifacts.RequiresPreviousDeployedArtifactAsync(5, Commit, "BrowserRuntime-artifacts", Arg.Any<CancellationToken>())
            .Returns(requiredByContract);
    }

    private Task<PipelinePreflightOutcomeDto> LaunchAsync(PipelineYamlDefinition definition, string? commit = Commit)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (commit is not null) variables["AETHEUS_SOURCE_COMMIT"] = commit;
        return BuildSut().FindBlockingProblemsAsync(definition, variables, organizationId: 3, projectId: 5, Ct);
    }

    // --- Runner capability -------------------------------------------------------------------

    [Fact]
    public async Task ConfiguredRunnerOutsideTheProtocolWindow_RefusesTheLaunch()
    {
        Runner(AgentProtocol.MaximumSupportedVersion + 1, AgentCapabilities.PipelineBuild);

        var problem = Assert.Single((await LaunchAsync(Build())).Problems);

        Assert.Contains("Stage 'Build'", problem, StringComparison.Ordinal);
        Assert.Contains("'vps2577917' reports agent protocol", problem, StringComparison.Ordinal);
        Assert.Contains("Update or reinstall the agent", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredRunnerWithoutThePipelineCapability_RefusesTheLaunch()
    {
        Runner(AgentProtocol.CurrentVersion, AgentCapabilities.ShellExecution);

        var problem = Assert.Single((await LaunchAsync(Build())).Problems);

        Assert.Contains($"does not advertise {AgentCapabilities.PipelineBuild}", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapableRunnerThatIsOffline_IsNotALaunchError()
    {
        // The Runner() default reports the server offline: waiting for it is the scheduler's stand-by path.
        var outcome = await LaunchAsync(Build());

        Assert.Empty(outcome.Problems);
    }

    // --- Retained release artifacts ----------------------------------------------------------

    [Fact]
    public async Task RetainedV1WithoutTheArtifactItsContractRequires_RefusesTheCandidateBeforeCi()
    {
        ChildExists(Qa());
        RetainedV1(browserRuntimePresent: false, requiredByContract: true);

        var problem = Assert.Single((await LaunchAsync(Candidate())).Problems);

        Assert.Contains("aetheus-qa", problem, StringComparison.Ordinal);
        Assert.Contains("Restore exact retained V-1 browser runtime", problem, StringComparison.Ordinal);
        Assert.Contains("'previous-deployed' has no retained artifact 'BrowserRuntime-artifacts'", problem, StringComparison.Ordinal);
        Assert.Contains("allow_missing does not apply", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingArtifactWithoutAllowMissing_RefusesTheLaunch()
    {
        ChildExists(Qa(allowMissing: false));
        RetainedV1(browserRuntimePresent: false, requiredByContract: false);

        var problem = Assert.Single((await LaunchAsync(Candidate())).Problems);

        Assert.DoesNotContain("allow_missing does not apply", problem, StringComparison.Ordinal);
        Assert.Contains("BrowserRuntime-artifacts", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingArtifactThatTheContractDoesNotRequire_IsTheAcceptedBootstrap()
    {
        ChildExists(Qa());
        RetainedV1(browserRuntimePresent: false, requiredByContract: false);

        Assert.Empty((await LaunchAsync(Candidate())).Problems);
    }

    [Fact]
    public async Task RetainedArtifactPresent_IsAccepted()
    {
        ChildExists(Qa());
        RetainedV1(browserRuntimePresent: true, requiredByContract: true);

        Assert.Empty((await LaunchAsync(Candidate())).Problems);
    }

    [Fact]
    public async Task OnSuccessChild_IsNotJudgedOnTheLaunchCommit()
    {
        // A downstream run starts later, on its own commit: the launch cannot know what it resolves.
        ChildExists(Qa());
        RetainedV1(browserRuntimePresent: false, requiredByContract: true);

        Assert.Empty((await LaunchAsync(Candidate(onSuccess: true))).Problems);
    }

    // --- Refusal log (recette R2-040) ---------------------------------------------------------

    [Fact]
    public async Task Refusal_LogsEachProblemAsItsOwnShortEntry_PlusOneCountLine()
    {
        // Two stages the runner cannot serve, one with a name long enough to exceed the logged bound.
        Runner(AgentProtocol.CurrentVersion, AgentCapabilities.ShellExecution);
        var longStage = "Stage-" + new string('x', 700);
        var definition = new PipelineYamlDefinition
        {
            Name = "aetheus-nightly",
            Stages =
            [
                new PipelineStageDefinition { Name = "Build", Os = "linux", Steps = [new PipelineStepDefinition { Name = "build", Shell = "make" }] },
                new PipelineStageDefinition { Name = longStage, Os = "linux", Steps = [new PipelineStepDefinition { Name = "test", Shell = "make test" }] }
            ]
        };
        var logger = new CapturingLogger();
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["AETHEUS_SOURCE_COMMIT"] = Commit };

        var problems = (await BuildSut(logger).FindBlockingProblemsAsync(definition, variables, 3, 5, Ct)).Problems;

        Assert.Equal(2, problems.Count);
        var count = Assert.Single(logger.Entries, entry => entry.Attributes.ContainsKey("ProblemCount") && !entry.Attributes.ContainsKey("Problem"));
        Assert.Equal(LogLevel.Warning, count.Level);
        Assert.Equal(2, count.Attributes["ProblemCount"]);
        Assert.Equal("aetheus-nightly", count.Attributes["PipelineName"]);
        var perProblem = logger.Entries.Where(entry => entry.Attributes.ContainsKey("Problem")).ToList();
        Assert.Equal(2, perProblem.Count);
        Assert.All(perProblem, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        Assert.Equal([1, 2], perProblem.Select(entry => (int)entry.Attributes["ProblemNumber"]!));
        Assert.Equal(problems[0], perProblem[0].Attributes["Problem"]);
        var cut = (string)perProblem[1].Attributes["Problem"]!;
        Assert.True(problems[1].Length > 500);
        Assert.Equal(problems[1][..500] + " [...]", cut);
        // No entry carries the whole list in one attribute any more.
        Assert.DoesNotContain(logger.Entries, entry => entry.Attributes.ContainsKey("Problems"));
        Assert.Equal(3, logger.Entries.Count);
    }

    private sealed class CapturingLogger : ILogger<PipelineRunPreflightService>
    {
        public List<(LogLevel Level, Dictionary<string, object?> Attributes)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var attributes = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                : [];
            Entries.Add((logLevel, attributes));
        }
    }

    [Fact]
    public async Task LaunchWithoutAVerifiedCommit_LeavesCommitRelativeRestoresToTheStep()
    {
        ChildExists(Qa());
        RetainedV1(browserRuntimePresent: false, requiredByContract: true);

        Assert.Empty((await LaunchAsync(Candidate(), commit: null)).Problems);
    }
}
