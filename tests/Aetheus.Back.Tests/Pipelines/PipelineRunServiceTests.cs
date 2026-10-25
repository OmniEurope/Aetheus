// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text.Json;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.GitGraph;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class PipelineRunServiceTests
{
    [Fact]
    public void CandidateScannerPreflight_RequiresExactAgentManifestHash()
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UPSTREAM_PIPELINE"] = "aetheus-candidate"
        };
        var missing = new Server { ScannerCapabilitiesJson = "[]" };
        var drifted = new Server
        {
            ScannerCapabilitiesJson = JsonSerializer.Serialize(new[]
            {
                $"scanner-manifest:sha256:{new string('0', 64)}"
            })
        };
        var exact = new Server
        {
            ScannerCapabilitiesJson = JsonSerializer.Serialize(new[]
            {
                $"scanner-manifest:sha256:{ScannerManifestCatalog.Sha256}"
            })
        };

        Assert.False(PipelineScannerTaskFactory.HasCompatibleScannerManifest(missing, variables));
        Assert.False(PipelineScannerTaskFactory.HasCompatibleScannerManifest(drifted, variables));
        Assert.True(PipelineScannerTaskFactory.HasCompatibleScannerManifest(exact, variables));
        Assert.True(PipelineScannerTaskFactory.HasCompatibleScannerManifest(missing, new Dictionary<string, string>()));
    }

    /// <summary>The child of any project's candidate (the wizard's portfolio-candidate, extending
    /// application-candidate) needs the exact manifest too.</summary>
    [Fact]
    public void CandidateScannerPreflight_AppliesToTheChildOfAnyPipelineExtendingTheCandidateTemplate()
    {
        var candidateChild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UPSTREAM_PIPELINE"] = "portfolio-candidate",
            [PipelineScannerTaskFactory.UpstreamTemplateVariable] = "application-candidate"
        };
        var nightlyChild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UPSTREAM_PIPELINE"] = "portfolio-nightly",
            [PipelineScannerTaskFactory.UpstreamTemplateVariable] = "application-nightly"
        };
        var missing = new Server { ScannerCapabilitiesJson = "[]" };

        Assert.False(PipelineScannerTaskFactory.HasCompatibleScannerManifest(missing, candidateChild));
        Assert.True(PipelineScannerTaskFactory.HasCompatibleScannerManifest(missing, nightlyChild));
    }

    [Fact]
    public void BuildTriggerStepIdempotencyKey_IsStableAndStepScoped()
    {
        var first = PipelineTriggerStepCoordinator.BuildTriggerStepIdempotencyKey(415, 9001);
        var replay = PipelineTriggerStepCoordinator.BuildTriggerStepIdempotencyKey(415, 9001);
        var sibling = PipelineTriggerStepCoordinator.BuildTriggerStepIdempotencyKey(415, 9002);

        Assert.Equal("trigger-step:415:9001", first);
        Assert.Equal(first, replay);
        Assert.NotEqual(first, sibling);
    }

    private const string DefaultCommit = "0123456789abcdef0123456789abcdef01234567";
    private readonly IPipelineRepository _repoMock = Substitute.For<IPipelineRepository>();
    private readonly IHubContext<PipelineHub> _hubMock = Substitute.For<IHubContext<PipelineHub>>();
    private readonly IVariableLibraryService _varLibMock = Substitute.For<IVariableLibraryService>();
    private readonly IVaultService _vaultMock = Substitute.For<IVaultService>();
    private readonly IReleaseService _releaseServiceMock = Substitute.For<IReleaseService>();
    private readonly ISecretMaskingService _secretMaskingMock = Substitute.For<ISecretMaskingService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly IPipelineBranchAdvanceStep _branchAdvanceStepMock = Substitute.For<IPipelineBranchAdvanceStep>();
    private readonly IUserNotificationService _userNotifications = Substitute.For<IUserNotificationService>();
    private readonly ILogger<PipelineRunService> _loggerMock = Substitute.For<ILogger<PipelineRunService>>();
    private readonly IPipelineGitService _pipelineGitMock = Substitute.For<IPipelineGitService>();
    private readonly IProjectConfigRepositoryService _configRepositoryMock = Substitute.For<IProjectConfigRepositoryService>();
    private readonly IPipelineWorkspaceSourceResolver _workspaceSourcesMock = Substitute.For<IPipelineWorkspaceSourceResolver>();
    private readonly Aetheus.Back.Components.Artifacts.IArtifactRepository _artifactRepoMock = Substitute.For<Aetheus.Back.Components.Artifacts.IArtifactRepository>();
    private readonly IArtifactStorageService _artifactStorageMock = Substitute.For<IArtifactStorageService>();
    private readonly IHttpClientFactory _httpClientFactoryMock = Substitute.For<IHttpClientFactory>();
    private readonly IPostgresLeaderLease _operationLockMock = Substitute.For<IPostgresLeaderLease>();
    // Null in the engine every test shares; a test that exercises env_template sets it and builds its own engine.
    private IPipelineEnvFilePreparer? _envFiles;
    private readonly IPipelineVariableResolver _variableResolverMock;
    private readonly IClientProxy _clientProxyMock = Substitute.For<IClientProxy>();
    private readonly PipelineRunService _sut;

    // The same instance the engine was built with, so the checkpoint tests exercise the real
    // collaborator rather than a second one wired differently.
    private readonly PipelineCheckpointReuseService _checkpoints;

    // The test retains the reservation service to assert the resolved organization, which defines
    // the tenancy boundary.
    private IPipelinePortRegistryGuard _portGuardMock = Substitute.For<IPipelinePortRegistryGuard>();

    public PipelineRunServiceTests()
    {
        // The run detail reads its result figures apart; none recorded by default.
        _repoMock.GetRunResultSummariesAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRunResultSummaries(null, null, [], []));
        _operationLockMock.RunSerializedAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.ArgAt<CancellationToken>(2)));
        _repoMock.TryResolveTriggeredStepAsync(
                Arg.Any<int>(),
                Arg.Any<TaskExecutionStatus>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _clientProxyMock
            .SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var clientsMock = Substitute.For<IHubClients>();
        clientsMock.All.Returns(_clientProxyMock);
        clientsMock.Group(Arg.Any<string>()).Returns(_clientProxyMock);
        clientsMock.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(_clientProxyMock);
        _hubMock.Clients.Returns(clientsMock);

        _releaseServiceMock
            .NotifyPipelineRunCompletedAsync(Arg.Any<int>(), Arg.Any<PipelineStatus>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Default: run is active (Running) so AdvanceStageAsync proceeds.
        _repoMock.IsRunStillRunningAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetCompletedStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());
        _repoMock.GetTerminalStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());
        _repoMock.GetRunAffinityServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((int?)null);
        _repoMock.GetStageProducerServerIdAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((int?)null);
        _repoMock.FindOnlineServerByIdAsync(Arg.Any<int>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns((Server?)null);
        _repoMock.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetSuccessfulStepOutputsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<StepOutputProjection>());
        _repoMock.GetTriggeredChildRunIdsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        // Default: no linked run to roll a grade up from - echoes the run back unchanged, same as
        // production PipelineRunGradeAggregation.ApplyAsync when nothing resolves. Individual grade
        // tests override this to prove the roll-up itself.
        _repoMock.HydrateLinkedGradeAsync(Arg.Any<PipelineRunDto>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<PipelineRunDto>());
        // Production returns true for non-scanner steps; individual scanner retry tests can override it.
        _repoMock.IsStepRetryEligibleAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var domainEventsMock = Substitute.For<IDomainEventDispatcher>();
        domainEventsMock
            .DispatchAsync(Arg.Any<Aetheus.Back.Components.Pipelines.Events.PipelineRunCompletedEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _httpClientFactoryMock.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        _pipelineGitMock.ReadProjectPipelineYamlAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _repoMock.GetPipelineProjectIdAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>()).Returns(7);
        _pipelineGitMock.GetPipelineSourceAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<int?>())
            .Returns(new PipelineSourceDto
            {
                RepositoryId = 11,
                CloneUrl = "https://git.example.test/org/repository.git",
                Branch = "main",
                CommitHash = DefaultCommit
            });

        // Real variable resolver wired with mocked dependencies (tests exercise the resolution logic).
        var configMock = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitLight:RunTokenKey"] = "pipeline-run-service-tests-signing-key"
            })
            .Build();
        _variableResolverMock = new PipelineVariableResolver(_varLibMock, _vaultMock, _repoMock, configMock, TimeProvider.System);

        // Encryption pass-through: env-protection round-trips are covered by TaskEnvProtection tests.
        var encryptionMock = Substitute.For<Aetheus.Back.Services.IEncryptionService>();
        encryptionMock.EncryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        encryptionMock.DecryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());

        var gitGraphMock = Substitute.For<IGitGraphRecorder>();
        gitGraphMock.ResolveRunLinksAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((new List<CommitLinkDto>(), new List<BranchLinkDto>()));

        _sut = BuildSut(configMock, _pipelineGitMock, encryptionMock, gitGraphMock, domainEventsMock, out _checkpoints);
    }

    /// <summary>
    /// Builds the engine with its real collaborators over the mocked repository, so these tests keep
    /// exercising the actual wiring rather than a graph of substitutes. One factory instead of three
    /// inline constructions: the breakdown re-signs this constructor at every palier, and three copies
    /// meant three edits and three chances to wire one of them differently from the DI graph.
    /// </summary>
    private PipelineRunService BuildSut(
        IConfiguration config,
        IPipelineGitService pipelineGit,
        IEncryptionService encryption,
        IGitGraphRecorder gitGraph,
        IDomainEventDispatcher domainEvents,
        out PipelineCheckpointReuseService checkpoints)
    {
        // The launch preflight refuses a stage whose selector matches no CONFIGURED server, which on a
        // bare substitute is every stage. These tests exercise dispatch, not fleet configuration, so
        // the default says "a server is configured"; whether it is online stays the mocked decision
        // each test makes, and that is the part the dispatch assertions are about.
        _repoMock.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<bool>(5) ? new List<int>() : [1]);

        var taskBuilder = new PipelineStepTaskBuilder(
            _repoMock, encryption, Substitute.For<ILogger<PipelineStepTaskBuilder>>());
        var templates = new PipelineTemplateResolver(_repoMock);
        var servers = new PipelineDispatchServerResolver(_repoMock);
        var parameters = new PipelineRunParameterResolver(_repoMock, templates, servers);
        var deployEnv = Substitute.For<Aetheus.Back.Components.AppMonitoring.IAppDeployEnvProvider>();
        var definitions = new PipelineRunDefinitionParser(
            Substitute.For<ILogger<PipelineRunDefinitionParser>>());
        var finalizer = new PipelineRunFinalizer(
            _repoMock, _hubMock, domainEvents, _secretMaskingMock, TimeProvider.System);
        var preparation = new PipelineRunPreparationService(
            _repoMock, pipelineGit, templates, parameters, Substitute.For<IGitCliService>(),
            _workspaceSourcesMock, config,
            Substitute.For<ILogger<PipelineRunPreparationService>>());

        checkpoints = new PipelineCheckpointReuseService(
            _repoMock, _artifactRepoMock, _artifactStorageMock, _auditMock, parameters, _operationLockMock);

        var triggerSteps = new PipelineTriggerStepCoordinator(
            _repoMock, pipelineGit, preparation, parameters, checkpoints, _authzMock, _auditMock,
            TimeProvider.System, Substitute.For<ILogger<PipelineTriggerStepCoordinator>>());

        var stepDispatcher = new PipelineStepTaskDispatcher(
            _repoMock, servers, taskBuilder, triggerSteps, _branchAdvanceStepMock,
            new PipelineAnalysisTaskFactory(taskBuilder),
            new PipelineDotnetTestTaskFactory(taskBuilder),
            new PipelineFunctionTaskFactory(taskBuilder, TimeProvider.System),
            new PipelineGateStatusTaskFactory(taskBuilder),
            new PipelineDeploymentTaskFactory(
                _repoMock, _artifactRepoMock, encryption, deployEnv, config,
                Substitute.For<ILogger<PipelineDeploymentTaskFactory>>(), TimeProvider.System),
            new PipelineHostOperationTaskFactory(
                _repoMock, pipelineGit, encryption, _secretMaskingMock, config, deployEnv,
                Substitute.For<ILogger<PipelineHostOperationTaskFactory>>(), TimeProvider.System),
            new PipelineArtifactTaskFactory(_repoMock, _artifactRepoMock, encryption, TimeProvider.System),
            new PipelineScannerTaskFactory(_repoMock, encryption, TimeProvider.System),
            Substitute.For<Aetheus.Back.Components.AiTasks.IAiTaskService>(),
            encryption, deployEnv, config, _secretMaskingMock, TimeProvider.System,
            Substitute.For<ILogger<PipelineStepTaskDispatcher>>(),
            _envFiles);

        var systemTasks = new PipelineSystemTaskFactory(
            _repoMock, servers, finalizer, definitions, _variableResolverMock,
            _secretMaskingMock, _auditMock, encryption, config, TimeProvider.System,
            Substitute.For<ILogger<PipelineSystemTaskFactory>>());

        var planner = new PipelineStageDispatchPlanner(
            _repoMock, servers,
            new PipelineEnvironmentCheckGuard(
                _repoMock, _httpClientFactoryMock, Substitute.For<ILogger<PipelineEnvironmentCheckGuard>>()),
            finalizer, systemTasks, stepDispatcher, _hubMock, domainEvents, TimeProvider.System,
            Substitute.For<ILogger<PipelineStageDispatchPlanner>>());

        var scheduler = new PipelineRunScheduler(
            _repoMock, _hubMock, _variableResolverMock, finalizer, definitions, systemTasks, planner,
            encryption, _operationLockMock, TimeProvider.System,
            Substitute.For<ILogger<PipelineRunScheduler>>());

        var runReader = new PipelineRunReader(_repoMock, gitGraph, _secretMaskingMock, _variableResolverMock);

        // Real preflight, not a stub: these tests launch runs whose stages carry no selector, which
        // is exactly the case the preflight leaves to dispatch. Substituting it would hide a
        // regression that starts refusing them.
        // The port registry is stubbed to "no conflict": these tests are about run orchestration, and
        // the registry has its own suite. The stub is explicit rather than auto-returned so a future
        // signature change surfaces here instead of silently refusing every launch.
        var portGuard = _portGuardMock;
        portGuard.FindPortConflictsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PortConflictReport.Empty));

        var preflight = new PipelineRunPreflightService(
            _repoMock, servers,
            new PipelineEnvironmentCheckGuard(
                _repoMock, _httpClientFactoryMock, Substitute.For<ILogger<PipelineEnvironmentCheckGuard>>()),
            portGuard,
            new PipelineChildPipelineResolver(
                Substitute.For<IPipelineTemplateResolver>(), Substitute.For<IPipelineVariableResolver>()),
            Substitute.For<IPipelineRequirementsChecker>(),
            new PipelineScannerManifestPreflight(_repoMock, servers),
            new PipelineReleaseArtifactPreflight(Substitute.For<Aetheus.Back.Components.Artifacts.IArtifactRepository>(), Substitute.For<Aetheus.Back.Components.Artifacts.IArtifactStorageService>()),
            new PipelineAiProfilePreflight(Substitute.For<Aetheus.Back.Components.AiTasks.IAiTaskService>()),
            Substitute.For<ILogger<PipelineRunPreflightService>>());

        var launcher = new PipelineRunLauncher(
            _repoMock, _hubMock, _variableResolverMock, _auditMock, _authzMock, gitGraph,
            config, runReader, planner, parameters, preparation, preflight,
            new PipelineConfigTemplatePreflight(pipelineGit, _configRepositoryMock), portGuard,
            new PipelineRefusedLaunchRecorder(
                _repoMock, _hubMock, _auditMock,
                new PipelineRunNotificationPublisher(_repoMock, _userNotifications), TimeProvider.System,
                Substitute.For<ILogger<PipelineRefusedLaunchRecorder>>()),
            TimeProvider.System,
            Substitute.For<ILogger<PipelineRunLauncher>>());

        return new PipelineRunService(
            _repoMock, _variableResolverMock, _loggerMock,
            finalizer, parameters, checkpoints, preparation,
            new PipelineRunControlService(_repoMock, _hubMock, definitions, _auditMock, TimeProvider.System),
            triggerSteps, launcher, scheduler, preflight,
            new PipelineAdvisoryPreflightBuilder(
                _repoMock, servers,
                new PipelineChildPipelineResolver(
                    Substitute.For<IPipelineTemplateResolver>(), Substitute.For<IPipelineVariableResolver>()),
                Substitute.For<ILogger<PipelineAdvisoryPreflightBuilder>>()),
            runReader, definitions);
    }

    /// <summary>A second engine over the same mocked repository, for a test that changes a collaborator the
    /// shared engine is built without (<see cref="_envFiles"/>).</summary>
    private PipelineRunService BuildSutWithOwnCollaborators()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitLight:RunTokenKey"] = "pipeline-run-service-tests-signing-key"
            })
            .Build();
        var encryption = Substitute.For<Aetheus.Back.Services.IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        encryption.DecryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var gitGraph = Substitute.For<IGitGraphRecorder>();
        gitGraph.ResolveRunLinksAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((new List<CommitLinkDto>(), new List<BranchLinkDto>()));
        var domainEvents = Substitute.For<IDomainEventDispatcher>();
        domainEvents
            .DispatchAsync(Arg.Any<Aetheus.Back.Components.Pipelines.Events.PipelineRunCompletedEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return BuildSut(config, _pipelineGitMock, encryption, gitGraph, domainEvents, out _);
    }
    // --- TriggerRunAsync ---

    [Fact]
    public async Task PrepareRunAsync_PinnedTemplateSnapshotRemainsOnVersionOneAfterVersionTwoPublished()
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline
        {
            Id = 1,
            Name = "toto",
            YamlDefinition = "name: toto\nextends: ci@1\nstages: []"
        });
        _repoMock.GetPipelineOrganizationIdAsync(1, Arg.Any<CancellationToken>()).Returns(4);
        var template = new PipelineTemplate
        {
            Id = 7,
            Name = "ci",
            OrganizationId = 4,
            LatestVersion = 1,
            Versions =
            [
                new PipelineTemplateVersion
                {
                    TemplateId = 7,
                    Version = 1,
                    YamlContent = "name: ci\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: echo v1"
                }
            ]
        };
        _repoMock.FindTemplateByNameAsync("ci", 4, Arg.Any<CancellationToken>()).Returns(template);

        var beforePublication = await _sut.PrepareRunAsync(1, ct: TestContext.Current.CancellationToken);
        template.LatestVersion = 2;
        template.Versions.Add(new PipelineTemplateVersion
        {
            TemplateId = 7,
            Version = 2,
            YamlContent = "name: ci\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: echo v2"
        });
        var afterPublication = await _sut.PrepareRunAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(beforePublication);
        Assert.NotNull(afterPublication);
        Assert.Contains("echo v1", beforePublication.YamlSnapshot, StringComparison.Ordinal);
        Assert.Contains("echo v1", afterPublication.YamlSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("echo v2", afterPublication.YamlSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRunParametersAsync_ReadsSelectedSourceBranch()
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline
        {
            Id = 1,
            ProjectId = 7,
            Name = "release",
            SourceBranch = "main",
            YamlDefinition = "name: release\nstages: []"
        });
        _pipelineGitMock.ReadProjectPipelineYamlAsync(
                7, "release", Arg.Any<CancellationToken>(), "release/2026.07")
            .Returns("name: release\nparameters:\n  - name: target\nstages: []");

        var parameters = await _sut.GetRunParametersAsync(1, "release/2026.07", ct: TestContext.Current.CancellationToken);

        Assert.Equal("target", Assert.Single(parameters).Name);
        await _pipelineGitMock.Received(1).ReadProjectPipelineYamlAsync(
            7, "release", Arg.Any<CancellationToken>(), "release/2026.07");
    }

    /// <summary>French parameter labels and help accompany default text.</summary>
    [Fact]
    public async Task GetRunParametersAsync_CarriesTheFrenchTextBesideTheDefault()
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline
        {
            Id = 1,
            ProjectId = 7,
            Name = "release",
            SourceBranch = "main",
            YamlDefinition = "name: release\nstages: []"
        });
        _pipelineGitMock.ReadProjectPipelineYamlAsync(7, "release", Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns("""
                name: release
                parameters:
                  - name: target
                    display_name: Target
                    display_name_fr: Cible
                    description: Where it goes
                    description_fr: Où cela part
                  - name: plain
                stages: []
                """);

        var parameters = await _sut.GetRunParametersAsync(1, ct: TestContext.Current.CancellationToken);

        var target = parameters.Single(p => p.Name == "target");
        Assert.Equal(("Target", "Cible", "Where it goes", "Où cela part"),
            (target.DisplayName, target.DisplayNameFr, target.Description, target.DescriptionFr));
        var plain = parameters.Single(p => p.Name == "plain");
        Assert.Null(plain.DisplayNameFr);
        Assert.Null(plain.DescriptionFr);
    }

    [Fact]
    public async Task TriggerRunAsync_PipelineNotFound_ReturnsNull()
    {
        _repoMock.FindPipelineAsync(99, Arg.Any<CancellationToken>())
            .Returns((Pipeline?)null);

        var result = await _sut.TriggerRunAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task TriggerRunAsync_InvalidYaml_IsRejected()
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Bad", YamlDefinition = "{{invalid", Runs = [] });

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TriggerRunAsync_MalformedContainerLimit_FailsBeforeRunCreation()
    {
        var yaml = """
            name: deploy
            isolation:
              mode: container
              image: alpine:3.22@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
              cpus: nope
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = yaml, Runs = [] });

        var error = await Assert.ThrowsAsync<BadRequestException>(() => _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken));

        Assert.Contains("CPU limit", error.Message, StringComparison.OrdinalIgnoreCase);
        await _repoMock.DidNotReceive().AddPipelineRunAsync(
            Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerRunAsync_ValidPipeline_CreatesRun()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = yaml, Runs = [] });
        _repoMock.AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.TrackPipelineStepRun(Arg.Any<PipelineStepRun>());
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Running,
                Pipeline = new Pipeline { Name = "Deploy" },
                StepRuns = []
            });

        var result = await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(PipelineStatus.Running, result.Status);
        await _repoMock.Received(1).AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
        _repoMock.Received(3).TrackPipelineStepRun(Arg.Any<PipelineStepRun>());
    }

    /// <summary> A definition carrying keys this backend does not know yet still launches, and the
    /// run's warnings name every skipped key. </summary>
    [Fact]
    public async Task TriggerRunAsync_UnknownKeys_LaunchesAndRecordsAWarningPerKey()
    {
        var yaml = """
            name: deploy
            trigger: manual
            future_option: on
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
                    future_step_option: 2
            """;

        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = yaml, Runs = [] });
        PipelineRun? captured = null;
        _repoMock.AddPipelineRunAsync(Arg.Do<PipelineRun>(run => captured = run), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            Pipeline = new Pipeline { Name = "Deploy" },
            StepRuns = []
        });

        var result = await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(captured);
        var warnings = System.Text.Json.JsonSerializer.Deserialize<List<string>>(captured!.WarningsJson!)!;
        Assert.Contains("Unknown top-level property 'future_option' will be ignored.", warnings);
        Assert.Contains("Unknown step property 'future_step_option' will be ignored.", warnings);
    }

    [Fact]
    public async Task TriggerRunAsync_EnvironmentOwnedPipeline_PinsEffectiveProjectCommit()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var yaml = """
            name: release
            trigger: manual
            stages:
              - name: deploy
                os: linux
                steps:
                  - name: run
                    shell: echo deploy
            """;
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "release", EnvironmentId = 9, YamlDefinition = yaml, Runs = [] });
        _repoMock.GetPipelineProjectIdAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>()).Returns(7);
        _pipelineGitMock.GetPipelineSourceAsync(
                7, "release", Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns(new PipelineSourceDto
            {
                RepositoryId = 11,
                CloneUrl = "https://git.example.test/org/repository.git",
                Branch = "main",
                CommitHash = commit
            });
        _pipelineGitMock.ReadProjectPipelineYamlAtRevisionAsync(7, "release", commit, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        PipelineRun? captured = null;
        _repoMock.AddPipelineRunAsync(Arg.Do<PipelineRun>(run => captured = run), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            Pipeline = new Pipeline { Name = "release" },
            StepRuns = []
        });

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(commit, captured!.CommitHash);
        var variables = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            captured.ResolvedVariablesJson!)!;
        Assert.Equal(commit, variables["BUILD_SOURCEVERSION"]);
        await _pipelineGitMock.Received(1).GetPipelineSourceAsync(
            7, "release", Arg.Any<CancellationToken>(), null, null);
    }

    /// <summary>
    /// F-006. The launcher read <c>Pipeline.Project?.OrganizationId</c>, which is null for a pipeline
    /// owned by an Environment or a ProjectServer. A null organization does not narrow the fleet, it
    /// opens it: the blocking preflight then validated against other organizations' servers, and the
    /// port reservation could be written on one of them, after which that organization's own
    /// legitimate deployments are refused for a port they never took. Resolved transitively now, as
    /// every other call site already did.
    /// </summary>
    [Fact]
    public async Task TriggerRunAsync_EnvironmentOwnedPipeline_ReservesPortsAgainstTheResolvedOrganization()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var yaml = """
            name: release
            trigger: manual
            stages:
              - name: deploy
                os: linux
                steps:
                  - name: run
                    shell: echo deploy
            """;
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "release", EnvironmentId = 9, YamlDefinition = yaml, Runs = [] });
        _repoMock.GetPipelineProjectIdAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>()).Returns(7);
        // Pipeline.Project is null here (the owner is environment 9); only the transitive lookup
        // can name the organization.
        _repoMock.GetPipelineOrganizationIdAsync(1, Arg.Any<CancellationToken>()).Returns(4);
        _pipelineGitMock.GetPipelineSourceAsync(
                7, "release", Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns(new PipelineSourceDto
            {
                RepositoryId = 11,
                CloneUrl = "https://git.example.test/org/repository.git",
                Branch = "main",
                CommitHash = commit
            });
        _pipelineGitMock.ReadProjectPipelineYamlAtRevisionAsync(7, "release", commit, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _repoMock.AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            Pipeline = new Pipeline { Name = "release" },
            StepRuns = []
        });

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        await _portGuardMock.Received(1).DeclareReservationsAsync(
            Arg.Any<PipelineYamlDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Is<int?>(organizationId => organizationId == 4), Arg.Any<int?>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _portGuardMock.DidNotReceive().DeclareReservationsAsync(
            Arg.Any<PipelineYamlDefinition>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Is<int?>(organizationId => organizationId == null), Arg.Any<int?>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repoMock.Received().GetPipelineOrganizationIdAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerRunAsync_SelectedSourceBranch_PinsAndRecordsThatBranch()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        const string yaml = "name: release\ntrigger: manual\nstages: []";
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline
        {
            Id = 1,
            Name = "release",
            ProjectId = 7,
            SourceBranch = "release/2026.07",
            YamlDefinition = yaml,
            Runs = []
        });
        _pipelineGitMock.GetPipelineSourceAsync(
                7, "release", Arg.Any<CancellationToken>(), "release/2026.07", Arg.Any<int?>())
            .Returns(new PipelineSourceDto
            {
                RepositoryId = 11,
                CloneUrl = "https://git.example.test/org/repository.git",
                Branch = "release/2026.07",
                CommitHash = commit
            });
        _pipelineGitMock.ReadProjectPipelineYamlAtRevisionAsync(7, "release", commit, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        PipelineRun? captured = null;
        _repoMock.AddPipelineRunAsync(Arg.Do<PipelineRun>(run => captured = run), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            Pipeline = new Pipeline { Name = "release" },
            StepRuns = []
        });

        await _sut.TriggerRunAsync(1, new Dictionary<string, string>
        {
            ["AETHEUS_RUN_BRANCH"] = "release/2026.07"
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("release/2026.07", captured!.BranchName);
        await _pipelineGitMock.Received(1).GetPipelineSourceAsync(
            7, "release", Arg.Any<CancellationToken>(), "release/2026.07", null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task R534_TriggerRunAsync_RecordsTheDefinitionRevision_OnlyWhenTheWorkspaceComesFromAnotherRepository(bool sourceBlock)
    {
        const string definitionCommit = "0123456789abcdef0123456789abcdef01234567";
        const string workspaceCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var yaml = "name: nightly-public\ntrigger: manual\nsource_branch: develop\n"
            + (sourceBlock ? "source:\n  repository: aetheus-public\n" : string.Empty)
            + "stages: []";
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline
        {
            Id = 1,
            Name = "nightly-public",
            ProjectId = 7,
            SourceRepositoryId = 11,
            YamlDefinition = yaml,
            Runs = []
        });
        _pipelineGitMock.GetPipelineSourceAsync(7, "nightly-public", Arg.Any<CancellationToken>(), "develop", 11)
            .Returns(new PipelineSourceDto
            {
                RepositoryId = 11,
                CloneUrl = "https://git.example.test/git/7/aetheus.git",
                Branch = "develop",
                CommitHash = definitionCommit
            });
        _pipelineGitMock.ReadProjectPipelineYamlAtRevisionAsync(7, "nightly-public", definitionCommit, Arg.Any<CancellationToken>(), 11)
            .Returns((string?)null);
        _workspaceSourcesMock.ResolveAsync(
                7, Arg.Any<PipelineSourceDefinition>(), 11, definitionCommit, Arg.Any<CancellationToken>())
            .Returns(new PipelineWorkspaceSource(20, "https://git.example.test/git/7/aetheus-public.git", "main", workspaceCommit));
        PipelineRun? captured = null;
        _repoMock.AddPipelineRunAsync(Arg.Do<PipelineRun>(run => captured = run), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            Pipeline = new Pipeline { Name = "nightly-public" },
            StepRuns = []
        });

        // A caller cannot say where the definition came from: only the preparation does.
        await _sut.TriggerRunAsync(1, new Dictionary<string, string>
        {
            ["AETHEUS_DEFINITION_COMMIT"] = "cccccccccccccccccccccccccccccccccccccccc"
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        var variables = PipelineRunService.DeserializeResolvedVariablesStatic(captured!.AdditionalVariablesJson);
        if (sourceBlock)
        {
            Assert.Equal(("main", workspaceCommit, "https://git.example.test/git/7/aetheus-public.git"),
                (captured.BranchName, captured.CommitHash, captured.RepositoryUrl));
            Assert.Equal(definitionCommit, variables["AETHEUS_DEFINITION_COMMIT"]);
            Assert.Equal("develop", variables["AETHEUS_DEFINITION_BRANCH"]);
            Assert.Equal(definitionCommit, PipelineRunService.ResolveDefinitionCommit(captured));
        }
        else
        {
            Assert.Equal(("develop", definitionCommit), (captured.BranchName, captured.CommitHash));
            Assert.False(variables.ContainsKey("AETHEUS_DEFINITION_COMMIT"));
            Assert.Equal(definitionCommit, PipelineRunService.ResolveDefinitionCommit(captured));
        }
    }

    [Fact]
    public async Task TriggerRunAsync_CanonicalYamlSourceBranch_OverridesStaleDatabaseBranch()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        const string yaml = "name: nightly\ntrigger: schedule\nsource_branch: develop\nstages: []";
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline
        {
            Id = 1,
            Name = "nightly",
            ProjectId = 7,
            SourceBranch = "main",
            YamlDefinition = yaml,
            Project = new Project
            {
                Id = 7,
                Name = "Aetheus",
                DefaultBranch = "main",
                OrganizationId = 1
            },
            Runs = []
        });
        _pipelineGitMock.GetPipelineSourceAsync(
                7, "nightly", Arg.Any<CancellationToken>(), "develop", Arg.Any<int?>())
            .Returns(new PipelineSourceDto
            {
                RepositoryId = 11,
                CloneUrl = "https://git.example.test/org/repository.git",
                Branch = "develop",
                CommitHash = commit
            });
        _pipelineGitMock.ReadProjectPipelineYamlAtRevisionAsync(7, "nightly", commit, Arg.Any<CancellationToken>())
            .Returns(yaml);
        PipelineRun? captured = null;
        _repoMock.AddPipelineRunAsync(Arg.Do<PipelineRun>(run => captured = run), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            Pipeline = new Pipeline { Name = "nightly" },
            StepRuns = []
        });

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("develop", captured!.BranchName);
        Assert.Equal(commit, captured.CommitHash);
        var variables = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            captured.ResolvedVariablesJson!)!;
        Assert.Equal("develop", variables["DEFAULT_BRANCH"]);
        Assert.Equal("develop", variables["BUILD_SOURCEBRANCH"]);
    }

    [Fact]
    public async Task TriggerRunAsync_SnapshotResolutionThrows_DoesNotPersistUnpreparedRun()
    {
        // Git resolution happens before authorization. A snapshot that could not be resolved must not
        // create a run row because its exact target servers have not been authorized yet.
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", ProjectId = 5, YamlDefinition = yaml, Runs = [] });
        _repoMock.AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 0,
                PipelineId = 1,
                Status = PipelineStatus.Failed,
                Pipeline = new Pipeline { Name = "Deploy" },
                StepRuns = []
            });

        var pipelineGitMock = Substitute.For<IPipelineGitService>();
        pipelineGitMock.ReadProjectPipelineYamlAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        pipelineGitMock.GetPipelineSourceAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<int?>())
            .Returns(Task.FromException<PipelineSourceDto?>(new InvalidOperationException("git exploded")));

        var httpClientFactoryMock = Substitute.For<IHttpClientFactory>();
        httpClientFactoryMock.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var domainEventsMock = Substitute.For<IDomainEventDispatcher>();
        var gitGraphMock = Substitute.For<IGitGraphRecorder>();
        var artifactRepoMock = Substitute.For<Aetheus.Back.Components.Artifacts.IArtifactRepository>();
        var encryptionMock = Substitute.For<Aetheus.Back.Services.IEncryptionService>();
        encryptionMock.EncryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        encryptionMock.DecryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        var sut = BuildSut(config, pipelineGitMock, encryptionMock, gitGraphMock, domainEventsMock, out _);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken));

        Assert.Equal("git exploded", error.Message);
        await _repoMock.DidNotReceive().AddPipelineRunAsync(
            Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerRunAsync_RehomesProjectRepositoryUrlOntoCloneBase()
    {
        // Regression: the first-stage clone task is created at trigger time, so the project's
        // attach-time mirror URL (frozen on localhost) must be re-homed onto GitLight:CloneBaseUrl -
        // otherwise a containerised agent (where localhost is the container) cannot reach the git
        // server and `git clone` fails with exit 128. See MirrorCloneUrl.Rehome.
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitLight:CloneBaseUrl"] = "http://host.docker.internal:5300"
            })
            .Build();

        var httpClientFactoryMock = Substitute.For<IHttpClientFactory>();
        httpClientFactoryMock.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var pipelineGitMock = Substitute.For<IPipelineGitService>();
        pipelineGitMock.ReadProjectPipelineYamlAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        pipelineGitMock.GetPipelineSourceAsync(
                1, "Deploy", Arg.Any<CancellationToken>(), "master", 11)
            .Returns(new PipelineSourceDto
            {
                RepositoryId = 11,
                CloneUrl = "http://localhost:5300/git/1/toto.git",
                Branch = "master",
                CommitHash = DefaultCommit
            });
        var domainEventsMock = Substitute.For<IDomainEventDispatcher>();
        var gitGraphMock = Substitute.For<IGitGraphRecorder>();
        var artifactRepoMock = Substitute.For<Aetheus.Back.Components.Artifacts.IArtifactRepository>();
        var encryptionMock = Substitute.For<Aetheus.Back.Services.IEncryptionService>();
        encryptionMock.EncryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        encryptionMock.DecryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>());

        var sut = BuildSut(config, pipelineGitMock, encryptionMock, gitGraphMock, domainEventsMock, out _);

        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    checkout: true
                    shell: dotnet build
            """;
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline
            {
                Id = 1,
                Name = "Deploy",
                YamlDefinition = yaml,
                Runs = [],
                ProjectId = 1,
                SourceRepositoryId = 11,
                Project = new Project
                {
                    Id = 1,
                    Name = "Toto",
                    DefaultBranch = "master",
                    RepositoryUrl = "http://localhost:5300/git/1/toto.git"
                }
            });
        PipelineRun? captured = null;
        _repoMock.AddPipelineRunAsync(Arg.Do<PipelineRun>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 1, PipelineId = 1, Status = PipelineStatus.Running, Pipeline = new Pipeline { Name = "Deploy" }, StepRuns = [] });

        await sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        var vars = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(captured!.ResolvedVariablesJson!)!;
        Assert.Equal("http://host.docker.internal:5300/git/1/toto.git", vars["REPOSITORY_URL"]);
        Assert.Equal("http://host.docker.internal:5300/git/1/toto.git", vars["BUILD_REPOSITORY_URI"]);
    }

    // --- F-EXEC-1b: TriggerAutomatedRunAsync (webhook / scheduler, no caller principal) ---

    private const string OwnedYaml = """
        name: deploy
        trigger: manual
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: compile
                shell: dotnet build
        """;

    [Fact]
    public async Task TriggerAutomatedRunAsync_UnownedPipeline_ReturnsNullAndDoesNotCreateRun()
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Legacy", YamlDefinition = OwnedYaml, CreatedByUsername = null, Runs = [] });

        var result = await _sut.TriggerAutomatedRunAsync(1, "Webhook", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _repoMock.DidNotReceive().AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("BlockedUnownedAutomatedRun", "Pipeline", Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerAutomatedRunAsync_OwnerLacksServerAdmin_ReturnsNullAndDoesNotCreateRun()
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = OwnedYaml, CreatedByUsername = "alice", Runs = [] });
        _repoMock.FindCandidateTargetServerIdsAsync(
                null, null, "linux-01", OsType.Unknown, Arg.Is<int?>(organization => organization == null), false, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([42]);
        _authzMock.HasPermissionAsync("alice", ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.TriggerAutomatedRunAsync(1, "Scheduler", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _repoMock.DidNotReceive().AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerAutomatedRunAsync_OwnerAdministersAllTargets_DelegatesAndCreatesRun()
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = OwnedYaml, CreatedByUsername = "alice", Runs = [] });
        _repoMock.FindCandidateTargetServerIdsAsync(
                null, null, "linux-01", OsType.Unknown, Arg.Is<int?>(organization => organization == null), false, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([42]);
        _authzMock.HasPermissionAsync("alice", ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.TrackPipelineStepRun(Arg.Any<PipelineStepRun>());
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Running,
                Pipeline = new Pipeline { Name = "Deploy" },
                StepRuns = []
            });

        var result = await _sut.TriggerAutomatedRunAsync(1, "Webhook", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _repoMock.Received(1).AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerAutomatedRunAsync_ARefusedLaunch_TellsTheProjectSubscribers_AndStillThrows()
    {
        // An unsatisfiable reference refuses automated launch before a run exists, so the refusal
        // must be recorded.
        var yaml = OwnedYaml.Replace("shell: dotnet build", "shell: deploy $(DEMO_DOMAIN_THAT_NO_LIBRARY_GIVES)", StringComparison.Ordinal);
        var pipeline = new Pipeline { Id = 1, Name = "aetheus-nightly", YamlDefinition = yaml, CreatedByUsername = "alice", Runs = [] };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(pipeline);
        _repoMock.FindCandidateTargetServerIdsAsync(
                null, null, "linux-01", OsType.Unknown, Arg.Is<int?>(organization => organization == null), false, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([42]);
        _authzMock.HasPermissionAsync("alice", ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetPipelineProjectIdAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>()).Returns(7);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.TriggerAutomatedRunAsync(1, "Scheduler", ct: TestContext.Current.CancellationToken));

        // The refusal is a run that failed at once and carries the reason, in the pipeline's own list.
        await _repoMock.Received(1).AddPipelineRunAsync(
            Arg.Is<PipelineRun>(run => run.PipelineId == 1
                && run.Status == PipelineStatus.Failed
                && run.CompletedAt == run.StartedAt
                && run.YamlSnapshot == yaml
                && run.WarningsJson!.Contains("The automated launch (Scheduler) was refused", StringComparison.Ordinal)
                && run.WarningsJson.Contains("DEMO_DOMAIN_THAT_NO_LIBRARY_GIVES", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync(
            "LaunchRefused", "PipelineRun", Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _userNotifications.Received(1).RecordProjectEventAsync(
            NotificationEventTypes.PipelineLaunchRefused,
            Arg.Is<string>(payload => payload.Contains("\"ProjectId\":7", StringComparison.Ordinal)
                && payload.Contains("Scheduler", StringComparison.Ordinal)
                && payload.Contains("DEMO_DOMAIN_THAT_NO_LIBRARY_GIVES", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerAutomatedRunAsync_ARefusedPreparation_LeavesAFailedRunCarryingTheReason_AndStillThrows()
    {
        // The definition itself cannot be prepared (here an invalid source branch): no preparation
        // exists, so the failed run carries no snapshot, only the reason.
        var pipeline = new Pipeline { Id = 1, Name = "aetheus-nightly", YamlDefinition = OwnedYaml, CreatedByUsername = "alice", Runs = [] };
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(pipeline);
        _repoMock.ReserveNextBuildNumberAsync(1, Arg.Any<CancellationToken>()).Returns(12);
        _repoMock.GetPipelineProjectIdAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>()).Returns(7);
        var variables = new Dictionary<string, string> { [PipelineRunService.SourceBranchVariable] = "bad branch name" };

        var refusal = await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.TriggerAutomatedRunAsync(1, "GitPush", variables, TestContext.Current.CancellationToken));

        await _repoMock.Received(1).AddPipelineRunAsync(
            Arg.Is<PipelineRun>(run => run.PipelineId == 1
                && run.Status == PipelineStatus.Failed
                && run.BuildNumber == 12
                && run.YamlSnapshot == null
                && run.WarningsJson!.Contains("The automated launch (GitPush) was refused", StringComparison.Ordinal)
                && run.WarningsJson.Contains(refusal.Message, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _userNotifications.Received(1).RecordProjectEventAsync(
            NotificationEventTypes.PipelineLaunchRefused,
            Arg.Is<string>(payload => payload.Contains("GitPush", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    // --- GetRunsAsync ---

    [Fact]
    public async Task GetRunsAsync_ReturnsMappedPagedResult()
    {
        _repoMock.GetRunsPagedAsync(1, 1, 25, Arg.Any<PipelineRunPaginationRequest?>(), Arg.Any<CancellationToken>())
            .Returns((
                new List<PipelineRunDto>
                {
                    new()
                    {
                        Id = 1, PipelineId = 1, Status = PipelineStatus.Success,
                        PipelineName = "Deploy",
                        Steps = []
                    }
                },
                1));

        var result = await _sut.GetRunsAsync(1, new PipelineRunPaginationRequest(), ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.Equal(PipelineStatus.Success, result.Items[0].Status);
        Assert.Equal(1, result.TotalCount);
    }

    // --- GetRunAsync ---

    [Fact]
    public async Task GetRunAsync_Found_ReturnsDto()
    {
        _secretMaskingMock.MaskAsync(
                "Agent failed with secret-token.",
                1,
                Arg.Any<CancellationToken>())
            .Returns("Agent failed with ***.");
        _repoMock.GetRunDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Running,
                Pipeline = new Pipeline { Name = "CI" },
                StepRuns =
                [
                    new PipelineStepRun
                    {
                        Id = 1,
                        StepName = "build",
                        StageName = "stage1",
                        Task = new ServerTask
                        {
                            FailureCode = "ToolError",
                            FailureReason = "Agent failed with secret-token."
                        }
                    }
                ]
            });

        var result = await _sut.GetRunAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("CI", result.PipelineName);
        var step = Assert.Single(result.Steps);
        Assert.Equal("ToolError", step.FailureCode);
        Assert.Equal("Agent failed with ***.", step.FailureReason);
    }

    [Fact]
    public async Task GetRunAsync_SystemFailureWithoutTask_ReturnsMaskedDiagnostic()
    {
        _secretMaskingMock.MaskAsync(
                "No runner for secret-pool.",
                2,
                Arg.Any<CancellationToken>())
            .Returns("No runner for ***.");
        _repoMock.GetRunDetailAsync(2, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 2,
                PipelineId = 1,
                Pipeline = new Pipeline { Name = "CI" },
                StepRuns =
                [
                    new PipelineStepRun
                    {
                        Id = 2,
                        StepName = "restore",
                        StageName = "System:Prepare",
                        FailureCode = "InfrastructureMismatch",
                        FailureReason = "No runner for secret-pool."
                    }
                ]
            });

        var result = await _sut.GetRunAsync(2, ct: TestContext.Current.CancellationToken);

        var step = Assert.Single(Assert.IsType<PipelineRunDto>(result).Steps);
        Assert.Null(step.TaskId);
        Assert.Equal("InfrastructureMismatch", step.FailureCode);
        Assert.Equal("No runner for ***.", step.FailureReason);
    }

    [Fact]
    public async Task GetRunAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetRunDetailAsync(99, Arg.Any<CancellationToken>())
            .Returns((PipelineRun?)null);

        var result = await _sut.GetRunAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetRunAsync_AssignedStep_ExposesLiveQueuePositionAndCancellationMarker()
    {
        _repoMock.GetRunDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Running,
                AdditionalVariablesJson = "{\"__AETHEUS_CANCEL_REQUESTED\":\"true\"}",
                WarningsJson = "[\"Cancellation requested; always() teardown stages remain mandatory before the run becomes Cancelled.\",\"Cancellation requested; always() teardown stages remain mandatory before the run becomes Cancelled.\"]",
                Pipeline = new Pipeline { Name = "CI" },
                StepRuns =
                [
                    new PipelineStepRun
                    {
                        Id = 5,
                        StepName = "build",
                        StageName = "stage1",
                        Status = TaskExecutionStatus.Assigned,
                        TaskId = 40
                    }
                ]
            });
        _repoMock.GetTaskQueuePositionsAsync(
                Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 40 })),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, TaskQueuePosition> { [40] = new(3, 8) });

        var result = await _sut.GetRunAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result.CancellationRequested);
        Assert.Empty(result.Warnings);
        var step = Assert.Single(result.Steps);
        Assert.Equal(3, step.QueuePosition);
        Assert.Equal(8, step.QueueDepth);
    }

    [Fact]
    public async Task GetRunQueueStateAsync_UsesOnlyQueueReferencesAndPositions()
    {
        _repoMock.GetRunQueueReferencesAsync(7, Arg.Any<CancellationToken>())
            .Returns([new PipelineRunQueueReference(5, 40)]);
        _repoMock.GetTaskQueuePositionsAsync(
                Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 40 })),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, TaskQueuePosition> { [40] = new(2, 9) });

        var result = await _sut.GetRunQueueStateAsync(7, TestContext.Current.CancellationToken);

        var step = Assert.Single(result.Steps);
        Assert.Equal(7, result.RunId);
        Assert.Equal(5, step.StepId);
        Assert.Equal(2, step.Position);
        Assert.Equal(9, step.Depth);
        await _repoMock.DidNotReceive().GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveCompletedTriggerStepAsync_PersistsChildResultBeforeAdvancingParent()
    {
        var step = new PipelineStepRun
        {
            Id = 40,
            PipelineRunId = 12,
            StageName = "Security",
            StepName = "Run security child",
            Status = TaskExecutionStatus.Running,
            TriggeredRunId = 99
        };
        var outputs = new Dictionary<string, string>
        {
            ["CANDIDATE_VERSION"] = "c-source-123"
        };
        _repoMock.IsRunStillRunningAsync(12, Arg.Any<CancellationToken>()).Returns(false);

        var resolved = await _sut.ResolveCompletedTriggerStepAsync(
            step, PipelineStatus.Success, outputs, TestContext.Current.CancellationToken);

        Assert.True(resolved);
        await _repoMock.Received(1).TryResolveTriggeredStepAsync(
            40,
            TaskExecutionStatus.Success,
            0,
            Arg.Is<string?>(json =>
                json != null
                && PipelineRunHelpers.DeserializeResolvedVariables(json)["CANDIDATE_VERSION"] == "c-source-123"),
            Arg.Is<string?>(value => value == null),
            Arg.Is<string?>(value => value == null),
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).IsRunStillRunningAsync(12, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveCompletedTriggerStepAsync_DuplicateCompletionLosesAtomicTransition()
    {
        var step = new PipelineStepRun
        {
            Id = 41,
            PipelineRunId = 12,
            StageName = "Security",
            StepName = "Run security child",
            Status = TaskExecutionStatus.Running,
            TriggeredRunId = 99
        };
        _repoMock.TryResolveTriggeredStepAsync(
                41,
                Arg.Any<TaskExecutionStatus>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        var resolved = await _sut.ResolveCompletedTriggerStepAsync(
            step,
            PipelineStatus.Success,
            new Dictionary<string, string>(),
            TestContext.Current.CancellationToken);

        Assert.False(resolved);
        await _repoMock.DidNotReceive().IsRunStillRunningAsync(12, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveCompletedTriggerStepAsync_FailedChildPersistsDiagnostic()
    {
        var step = new PipelineStepRun
        {
            Id = 42,
            PipelineRunId = 12,
            StageName = "Security",
            StepName = "Run security child",
            Status = TaskExecutionStatus.Running,
            TriggeredRunId = 99
        };
        _repoMock.IsRunStillRunningAsync(12, Arg.Any<CancellationToken>()).Returns(false);

        var resolved = await _sut.ResolveCompletedTriggerStepAsync(
            step, PipelineStatus.Failed, new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        Assert.True(resolved);
        await _repoMock.Received(1).TryResolveTriggeredStepAsync(
            42,
            TaskExecutionStatus.Failed,
            1,
            null,
            TaskFailureCodes.ToolError,
            "Triggered run 99 completed with status Failed.",
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R1_80_ResolveCompletedTriggerStepAsync_RejectedChild_CompletesTheStepWithWhatItFound()
    {
        // Run 2634: the QA child's integration tests failed. The parent's trigger step is not a failure
        // (the candidate goes on to its seal), and it carries the gate status that rejects the parent.
        var step = new PipelineStepRun
        {
            Id = 43,
            PipelineRunId = 12,
            StageName = "QA",
            StepName = "Validate V",
            Status = TaskExecutionStatus.Running,
            TriggeredRunId = 99
        };
        var outputs = new Dictionary<string, string> { ["QA_CURRENT_INTEGRATION_GATE_STATUS"] = "1" };
        _repoMock.IsRunStillRunningAsync(12, Arg.Any<CancellationToken>()).Returns(false);

        var resolved = await _sut.ResolveCompletedTriggerStepAsync(
            step, PipelineStatus.Rejected, outputs, TestContext.Current.CancellationToken);

        Assert.True(resolved);
        await _repoMock.Received(1).TryResolveTriggeredStepAsync(
            43,
            TaskExecutionStatus.Success,
            0,
            Arg.Is<string?>(json =>
                json != null
                && PipelineRunHelpers.DeserializeResolvedVariables(json)["QA_CURRENT_INTEGRATION_GATE_STATUS"] == "1"),
            Arg.Is<string?>(value => value == null),
            Arg.Is<string?>(value => value == null),
            Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"QA_CURRENT_INTEGRATION_GATE_STATUS":"1"}""", true)]
    [InlineData("""{"UNIT_TEST_GATE_STATUS":"10"}""", true)]
    [InlineData("""{"CANDIDATE_DEPLOYABLE":"false"}""", true)]
    [InlineData("""{"QA_CURRENT_INTEGRATION_GATE_STATUS":"0"}""", false)]
    [InlineData("""{"CANDIDATE_DEPLOYABLE":"true"}""", false)]
    // An advisory smoke probe records findings without refusing anything.
    [InlineData("""{"SMOKE_GATE_STATUS":"1"}""", false)]
    public void R1_80_RejectsResult_ReadsTheSealsRequiredGatesOnly(string outputsJson, bool rejected)
    {
        Assert.Equal(rejected, PipelineControlVerdict.RejectsResult(PipelineRunHelpers.DeserializeResolvedVariables(outputsJson)));
    }

    // --- AdvanceStageAsync ---

    [Fact]
    public async Task AdvanceStageAsync_NotAllStepsDone_DoesNotAdvance()
    {
        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(false);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        await _repoMock.DidNotReceive().GetPipelineRunWithPipelineAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_RunNotFound_DoesNothing()
    {
        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns((PipelineRun?)null);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(Arg.Any<int>(), Arg.Any<PipelineStatus>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_AllDone_NoPendingSteps_CompletesWithSuccess()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 1, PipelineId = 1, Pipeline = new Pipeline { YamlDefinition = yaml } });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.UpdatePipelineRunStatusAsync(1, PipelineStatus.Success, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Success, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_AllDone_HasFailedStep_CompletesWithFailed()
    {
        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun { Id = 1, StageName = "build", StepName = "compile", Status = TaskExecutionStatus.Failed, RetryCount = 0, ContinueOnError = false }]);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_CancelledChildFailure_CompletesRequestedParentAsCancelled()
    {
        _repoMock.AreAllStepsInStageCompletedAsync(1, "orchestrate", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "orchestrate", Arg.Any<CancellationToken>())
            .Returns(
            [
                new PipelineStepRun
                {
                    Id = 1,
                    StageName = "orchestrate",
                    StepName = "child",
                    Status = TaskExecutionStatus.Failed,
                    RetryCount = 0,
                    ContinueOnError = false
                }
            ]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Running,
                AdditionalVariablesJson =
                    $"{{\"{PipelineRunHelpers.CancellationRequestedVariable}\":\"true\"}}",
                Pipeline = new Pipeline
                {
                    YamlDefinition = """
                        name: orchestrator
                        trigger: manual
                        stages:
                          - name: orchestrate
                            steps:
                              - name: child
                                type: trigger
                                pipeline: child
                        """
                }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([]);

        await _sut.AdvanceStageAsync(1, "orchestrate", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1,
            PipelineStatus.Cancelled,
            Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(
            1,
            PipelineStatus.Failed,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_FailedStage_WaitsForSystemCleanupBeforeFinalizing()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: restore
                agent: linux-01
                steps:
                  - name: restore-artifact
                    shell: exit 1
            """;
        var cleanup = new PipelineStepRun
        {
            Id = 2,
            PipelineRunId = 1,
            StageName = PipelineRunHelpers.SystemCleanupStage,
            StepName = "Cleanup",
            Status = TaskExecutionStatus.Pending,
            IsSystem = true
        };
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { YamlDefinition = yaml }
        };

        _repoMock.AreAllStepsInStageCompletedAsync(1, "restore", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "restore", Arg.Any<CancellationToken>()).Returns(
            [new PipelineStepRun { Id = 1, StageName = "restore", StepName = "restore-artifact", Status = TaskExecutionStatus.Failed }]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([cleanup]);
        _repoMock.GetRunAffinityServerIdAsync(1, Arg.Any<CancellationToken>()).Returns(10);
        _repoMock.FindOnlineServerByIdAsync(10, OsType.Unknown, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01", OsType = OsType.Linux });

        await _sut.AdvanceStageAsync(1, "restore", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "Cleanup"));
        Assert.Equal(TaskExecutionStatus.Assigned, cleanup.Status);
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_CleanupAffinityRunnerOffline_QueuesDurableCleanupAndFinalizesRun()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: restore
                agent: linux-01
                steps:
                  - name: restore-artifact
                    shell: exit 1
            """;
        var cleanup = new PipelineStepRun
        {
            Id = 2,
            PipelineRunId = 1,
            StageName = PipelineRunHelpers.SystemCleanupStage,
            StepName = "Cleanup",
            Status = TaskExecutionStatus.Pending,
            IsSystem = true
        };
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { YamlDefinition = yaml }
        };

        _repoMock.AreAllStepsInStageCompletedAsync(1, "restore", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "restore", Arg.Any<CancellationToken>()).Returns(
            [new PipelineStepRun { Id = 1, StageName = "restore", StepName = "restore-artifact", Status = TaskExecutionStatus.Failed }]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([cleanup]);
        _repoMock.GetRunAffinityServerIdAsync(1, Arg.Any<CancellationToken>()).Returns(10);
        _repoMock.FindOnlineServerByIdAsync(10, OsType.Unknown, Arg.Any<CancellationToken>()).Returns((Server?)null);
        _repoMock.FindServerByIdAsync(10, Arg.Any<CancellationToken>())
            .Returns(new Server
            {
                Id = 10,
                Name = "linux-01",
                OsType = OsType.Linux,
                Status = ServerStatus.Offline
            });

        await _sut.AdvanceStageAsync(1, "restore", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task =>
            task.ServerId == 10
            && task.PipelineRunId == 1
            && task.PipelineStepRunId == null
            && task.IsDeferredCleanup
            && task.Status == TaskExecutionStatus.Pending));
        await _repoMock.DidNotReceive().FindOnlineServerByAgentAsync(
            Arg.Any<string>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>());
        Assert.Equal(TaskExecutionStatus.Failed, cleanup.Status);
        await _auditMock.Received(1).LogAsync(
            "QueuedDeferredPipelineCleanup",
            "PipelineRun",
            1,
            "Runner 10",
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).AppendRunWarningsAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(warnings => warnings.Any(warning => warning.Contains("requires reconciliation"))),
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_Failure_DispatchesAlwaysStageBeforeFinalizing()
    {
        var yaml = """
            name: qa
            trigger: manual
            stages:
              - name: integration
                agent: linux-01
                steps:
                  - name: test
                    shell: dotnet test
              - name: e2e
                agent: linux-01
                depends_on: [integration]
                steps:
                  - name: browser-test
                    shell: dotnet test
              - name: teardown
                agent: linux-01
                condition: always()
                depends_on: [integration, e2e]
                steps:
                  - name: destroy
                    shell: docker compose down
            """;
        var teardown = new PipelineStepRun
        {
            Id = 2,
            PipelineRunId = 1,
            StageName = "teardown",
            StepName = "destroy",
            Status = TaskExecutionStatus.Pending
        };
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { YamlDefinition = yaml }
        };

        _repoMock.AreAllStepsInStageCompletedAsync(1, "e2e", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "e2e", Arg.Any<CancellationToken>()).Returns(
            [new PipelineStepRun { Id = 1, StageName = "e2e", StepName = "browser-test", Status = TaskExecutionStatus.Failed }]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([teardown]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>()).Returns([PipelineRunHelpers.SystemPrepareStage, "integration"]);
        _repoMock.GetTerminalStageNamesAsync(1, Arg.Any<CancellationToken>()).Returns([PipelineRunHelpers.SystemPrepareStage, "integration", "e2e"]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });

        await _sut.AdvanceStageAsync(1, "e2e", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "destroy"));
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    /// <summary>Rollback depends on the last guarded stage because failed() is evaluated once its
    /// dependencies settle. This keeps compensation eligible through evidence and commit.</summary>
    private const string CutoverWithRollbackYaml = """
        name: cutover
        trigger: manual
        stages:
          - name: Switch
            agent: linux-01
            steps:
              - name: point-traffic
                shell: switch.sh
          - name: Evidence
            agent: linux-01
            depends_on: [Switch]
            steps:
              - name: probe
                shell: smoke.sh
          - name: Commit
            agent: linux-01
            depends_on: [Evidence]
            steps:
              - name: close
                shell: commit.sh
          - name: Rollback
            agent: linux-01
            condition: "failed()"
            depends_on: [Commit]
            steps:
              - name: restore
                shell: rollback.sh
        """;

    [Fact]
    public async Task AdvanceStageAsync_SwitchSucceeded_LeavesTheRollbackStageReachable()
    {
        var evidence = new PipelineStepRun { Id = 2, PipelineRunId = 1, StageName = "Evidence", StepName = "probe" };
        var commit = new PipelineStepRun { Id = 3, PipelineRunId = 1, StageName = "Commit", StepName = "close" };
        var rollback = new PipelineStepRun { Id = 4, PipelineRunId = 1, StageName = "Rollback", StepName = "restore" };
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { YamlDefinition = CutoverWithRollbackYaml }
        };

        _repoMock.AreAllStepsInStageCompletedAsync(1, "Switch", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "Switch", Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([evidence, commit, rollback]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "Switch"]);
        // Commit has not settled, so the rollback that names it is not eligible yet.
        _repoMock.GetTerminalStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "Switch"]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });

        await _sut.AdvanceStageAsync(1, "Switch", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "probe"));
        Assert.Equal(TaskExecutionStatus.Pending, rollback.Status);
        Assert.Null(rollback.SkippedCondition);
    }

    [Fact]
    public async Task AdvanceStageAsync_EvidenceFailedAfterSwitch_DispatchesTheRollbackStage()
    {
        var commit = new PipelineStepRun { Id = 3, PipelineRunId = 1, StageName = "Commit", StepName = "close" };
        var rollback = new PipelineStepRun { Id = 4, PipelineRunId = 1, StageName = "Rollback", StepName = "restore" };
        var tracked = new[] { commit, rollback };
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { YamlDefinition = CutoverWithRollbackYaml }
        };

        _repoMock.AreAllStepsInStageCompletedAsync(1, "Evidence", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "Evidence", Arg.Any<CancellationToken>()).Returns(
            [new PipelineStepRun { Id = 2, StageName = "Evidence", StepName = "probe", Status = TaskExecutionStatus.Failed }]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        // Mirror the repository instead of freezing one answer: cancelling Commit is what makes it
        // terminal, and that is what the rollback naming it waits for.
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => tracked.Where(step => step.Status == TaskExecutionStatus.Pending).ToList());
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => new List<string> { PipelineRunHelpers.SystemPrepareStage, "Switch" });
        _repoMock.GetTerminalStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => new List<string> { PipelineRunHelpers.SystemPrepareStage, "Switch", "Evidence" }
                .Concat(tracked.Where(step => step.Status != TaskExecutionStatus.Pending).Select(step => step.StageName))
                .ToList());
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });

        await _sut.AdvanceStageAsync(1, "Evidence", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "restore"));
        Assert.Equal(TaskExecutionStatus.Cancelled, commit.Status);
    }

    /// <summary>A blue-green run whose failure came before the new colour was started.</summary>
    private const string FailedBeforeUpYaml = """
        name: deploy
        trigger: manual
        stages:
          - name: Verify
            agent: linux-01
            steps:
              - name: verify
                shell: verify.sh
          - name: Up
            agent: linux-01
            depends_on: [Verify]
            steps:
              - name: start
                type: bluegreen-up
          - name: Commit
            agent: linux-01
            depends_on: [Up]
            steps:
              - name: close
                shell: commit.sh
          - name: Rollback
            agent: linux-01
            condition: "failed()"
            depends_on: [Commit]
            steps:
              - name: restore
                type: bluegreen-rollback
        """;

    /// <summary>Recette R-14: the same run when the colour is started and restored by scripts, the stages
    /// saying it with <c>deployment_role</c> (release-fast).</summary>
    private const string ScriptedFailedBeforeUpYaml = """
        name: deploy
        trigger: manual
        stages:
          - name: Verify
            agent: linux-01
            steps:
              - name: verify
                shell: verify.sh
          - name: Up
            agent: linux-01
            deployment_role: start
            depends_on: [Verify]
            steps:
              - name: start
                shell: deploy.sh
          - name: Commit
            agent: linux-01
            depends_on: [Up]
            steps:
              - name: close
                shell: commit.sh
          - name: Rollback
            agent: linux-01
            condition: "failed()"
            deployment_role: rollback
            depends_on: [Commit]
            steps:
              - name: restore
                shell: rollback.sh
        """;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task R1_2_TheRollback_IsSkipped_OnlyWhenNoStartStepEverBegan(bool upStarted, bool scripted)
    {
        // Nightly 2497 failed upstream, deployed nothing, and still ran Rollback.
        var yaml = scripted ? ScriptedFailedBeforeUpYaml : FailedBeforeUpYaml;
        var up = new PipelineStepRun { Id = 2, PipelineRunId = 1, StageName = "Up", StepName = "start" };
        var commit = new PipelineStepRun { Id = 3, PipelineRunId = 1, StageName = "Commit", StepName = "close" };
        var rollback = new PipelineStepRun { Id = 4, PipelineRunId = 1, StageName = "Rollback", StepName = "restore" };
        var tracked = new[] { up, commit, rollback };
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { YamlDefinition = yaml }
        };

        _repoMock.AreAllStepsInStageCompletedAsync(1, "Verify", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "Verify", Arg.Any<CancellationToken>()).Returns(
            [new PipelineStepRun { Id = 1, StageName = "Verify", StepName = "verify", Status = TaskExecutionStatus.Failed }]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => tracked.Where(step => step.Status == TaskExecutionStatus.Pending).ToList());
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => new List<string> { PipelineRunHelpers.SystemPrepareStage });
        _repoMock.GetTerminalStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => new List<string> { PipelineRunHelpers.SystemPrepareStage, "Verify" }
                .Concat(tracked.Where(step => step.Status != TaskExecutionStatus.Pending).Select(step => step.StageName))
                .ToList());
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.DidAnyStepStartInStagesAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(upStarted);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });

        await _sut.AdvanceStageAsync(1, "Verify", ct: TestContext.Current.CancellationToken);

        if (upStarted)
        {
            Assert.NotEqual(PipelineStageDispatchPlanner.NothingToUndoCondition, rollback.SkippedCondition);
        }
        else
        {
            Assert.Equal(TaskExecutionStatus.Cancelled, rollback.Status);
            Assert.Equal(PipelineStageDispatchPlanner.NothingToUndoCondition, rollback.SkippedCondition);
            _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(task => task.Name == "restore"));
        }
    }

    /// <summary>Self-deployment confirmation occurs before commit.</summary>
    private const string ConfirmedCutoverYaml = """
        name: cutover
        trigger: manual
        stages:
          - name: Evidence
            agent: linux-01
            steps:
              - name: probe
                shell: smoke.sh
          - name: Confirm
            agent: linux-01
            environment: prod
            approval_timeout_minutes: 10
            depends_on: [Evidence]
            steps:
              - name: confirmed
                shell: echo confirmed
          - name: Commit
            agent: linux-01
            depends_on: [Confirm]
            steps:
              - name: close
                shell: commit.sh
          - name: Rollback
            agent: linux-01
            condition: "failed()"
            depends_on: [Commit]
            steps:
              - name: restore
                shell: rollback.sh
        """;

    /// <summary>Refused or expired confirmation fails its stage and lets rollback run before
    /// terminal completion.</summary>
    [Theory]
    [InlineData(ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.TimedOut)]
    public async Task ApplyRefusalAsync_UnconfirmedStage_RunsTheRollbackInsteadOfClosingTheRun(ApprovalStatus decision)
    {
        var confirm = new PipelineStepRun { Id = 2, PipelineRunId = 1, StageName = "Confirm", StepName = "confirmed" };
        var commit = new PipelineStepRun { Id = 3, PipelineRunId = 1, StageName = "Commit", StepName = "close" };
        var rollback = new PipelineStepRun { Id = 4, PipelineRunId = 1, StageName = "Rollback", StepName = "restore" };
        var tracked = new[] { confirm, commit, rollback };
        _repoMock.GetApprovalsAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new PipelineApproval { Id = 7, PipelineRunId = 1, StageName = "Confirm", Status = decision, TimeoutMinutes = 10,
                ResolvedAt = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc) }
        ]);
        _repoMock.TryTransitionPipelineRunStatusAsync(1, PipelineStatus.WaitingForApproval, PipelineStatus.Running, Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.AreAllStepsInStageCompletedAsync(1, "Confirm", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "Confirm", Arg.Any<CancellationToken>())
            .Returns(_ => tracked.Where(step => step.StageName == "Confirm" && step.Status == TaskExecutionStatus.Failed).ToList());
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { YamlDefinition = ConfirmedCutoverYaml }
        });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => tracked.Where(step => step.Status == TaskExecutionStatus.Pending).ToList());
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => new List<string> { PipelineRunHelpers.SystemPrepareStage, "Evidence" });
        _repoMock.GetTerminalStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => new List<string> { PipelineRunHelpers.SystemPrepareStage, "Evidence" }
                .Concat(tracked.Where(step => step.Status != TaskExecutionStatus.Pending).Select(step => step.StageName))
                .ToList());
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });

        var outcome = await _sut.ApplyRefusalAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(PipelineStatus.Running, outcome);
        Assert.Equal(TaskExecutionStatus.Failed, confirm.Status);
        Assert.Equal(TaskExecutionStatus.Cancelled, commit.Status);
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "restore"));
        _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(task => task.Name == "confirmed" || task.Name == "close"));
        await _repoMock.DidNotReceive().TryTransitionPipelineRunStatusAsync(
            1, PipelineStatus.WaitingForApproval, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    /// <summary>An environment approval refuses the run before anything it guards ran: it still just fails.</summary>
    [Fact]
    public async Task ApplyRefusalAsync_EnvironmentApproval_FailsTheRunWithoutRunningAnything()
    {
        _repoMock.GetApprovalsAsync(1, Arg.Any<CancellationToken>()).Returns(
            [new PipelineApproval { Id = 8, PipelineRunId = 1, StageName = "Evidence", Status = ApprovalStatus.Rejected }]);
        _repoMock.TryTransitionPipelineRunStatusAsync(1, PipelineStatus.WaitingForApproval, PipelineStatus.Failed, Arg.Any<CancellationToken>())
            .Returns(true);

        var outcome = await _sut.ApplyRefusalAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(PipelineStatus.Failed, outcome);
        await _repoMock.DidNotReceive().GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>());
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    /// <summary>Recette R-39: blue-green with a confirmation window and its rollback (release-fast shape).</summary>
    private const string ConfirmedBlueGreenYaml = """
        name: release
        trigger: manual
        stages:
          - name: Up
            agent: linux-01
            steps:
              - name: start
                type: bluegreen-up
          - name: Confirm
            agent: linux-01
            approval_timeout_minutes: 10
            depends_on: [Up]
            steps:
              - name: confirmed
                shell: echo confirmed
          - name: Rollback
            agent: linux-01
            condition: "failed()"
            depends_on: [Confirm]
            steps:
              - name: restore
                type: bluegreen-rollback
        """;

    /// <summary>The engine state once the rollback of a missed confirmation finished: only the workspace
    /// cleanup is left. <paramref name="rollbackSucceeded"/> decides whether the previous version is back.</summary>
    private (PipelineRun Run, PipelineStepRun Cleanup) ArrangeRollbackOfAMissedConfirmation(
        bool rollbackSucceeded, ApprovalStatus decision = ApprovalStatus.TimedOut, DateTime? resumeDeadline = null)
    {
        var cleanup = new PipelineStepRun
        {
            Id = 9,
            PipelineRunId = 1,
            StageName = PipelineRunHelpers.SystemCleanupStage,
            StepName = "Cleanup",
            Status = TaskExecutionStatus.Pending,
            IsSystem = true
        };
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            ResolvedVariablesJson = "{}",
            ResumeDeadline = resumeDeadline,
            Pipeline = new Pipeline { YamlDefinition = ConfirmedBlueGreenYaml }
        };
        _repoMock.AreAllStepsInStageCompletedAsync(1, "Rollback", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "Rollback", Arg.Any<CancellationToken>()).Returns(rollbackSucceeded
            ? []
            : [new PipelineStepRun { Id = 4, StageName = "Rollback", StepName = "restore", Status = TaskExecutionStatus.Failed }]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => cleanup.Status == TaskExecutionStatus.Pending ? [cleanup] : []);
        _repoMock.GetApprovalsAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new PipelineApproval
            {
                Id = 7, PipelineRunId = 1, Scope = ApprovalScope.Pipeline, StageName = "Confirm", Status = decision,
                TimeoutMinutes = 10, ResolvedAt = DateTime.UtcNow.AddMinutes(-3)
            }
        ]);
        _repoMock.GetBlockingFailedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns(rollbackSucceeded ? ["Confirm"] : ["Confirm", "Rollback"]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        // Every user stage has settled: Confirm failed on its timeout, the rollback ran after it.
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>()).Returns(rollbackSucceeded
            ? [PipelineRunHelpers.SystemPrepareStage, "Up", "Rollback"]
            : [PipelineRunHelpers.SystemPrepareStage, "Up"]);
        _repoMock.GetTerminalStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "Up", "Confirm", "Rollback"]);
        _repoMock.DidStagesAllSucceedAsync(1, Arg.Is<IReadOnlyCollection<string>>(s => s.Contains("Up")), Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.DidStagesAllSucceedAsync(1, Arg.Is<IReadOnlyCollection<string>>(s => s.Contains("Rollback")), Arg.Any<CancellationToken>())
            .Returns(rollbackSucceeded);
        _repoMock.TryParkRunAwaitingResumeAsync(1, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetRunAffinityServerIdAsync(1, Arg.Any<CancellationToken>()).Returns(10);
        _repoMock.FindOnlineServerByIdAsync(10, OsType.Unknown, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01", OsType = OsType.Linux });
        return (run, cleanup);
    }

    [Fact]
    public async Task R39_AdvanceStageAsync_RolledBackAfterAMissedConfirmation_WaitsForAResumeInsteadOfEnding()
    {
        var (_, cleanup) = ArrangeRollbackOfAMissedConfirmation(rollbackSucceeded: true);

        await _sut.AdvanceStageAsync(1, "Rollback", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).TryParkRunAwaitingResumeAsync(
            1, Arg.Is<DateTime>(deadline => deadline > DateTime.UtcNow.AddHours(23) && deadline <= DateTime.UtcNow.AddHours(24)),
            Arg.Any<CancellationToken>());
        // The workspace stays for the resume: the cleanup is not dispatched and the run is not finalized.
        _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(task => task.Name == "Cleanup"));
        Assert.Equal(TaskExecutionStatus.Pending, cleanup.Status);
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, Arg.Any<PipelineStatus>(), Arg.Any<CancellationToken>());
        await _clientProxyMock.Received(1).SendCoreAsync(
            "PipelineRunAwaitingResume", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A rollback that failed keeps today's behaviour: the cleanup runs and the run fails.</summary>
    [Fact]
    public async Task R39_AdvanceStageAsync_FailedRollbackAfterAMissedConfirmation_DoesNotWait()
    {
        var (_, cleanup) = ArrangeRollbackOfAMissedConfirmation(rollbackSucceeded: false);

        await _sut.AdvanceStageAsync(1, "Rollback", ct: TestContext.Current.CancellationToken);

        await _repoMock.DidNotReceive().TryParkRunAwaitingResumeAsync(1, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "Cleanup"));
        Assert.Equal(TaskExecutionStatus.Assigned, cleanup.Status);
    }

    [Fact]
    public async Task R39_AdvanceStageAsync_RefusedConfirmation_DoesNotWait()
    {
        var (_, cleanup) = ArrangeRollbackOfAMissedConfirmation(rollbackSucceeded: true, decision: ApprovalStatus.Rejected);

        await _sut.AdvanceStageAsync(1, "Rollback", ct: TestContext.Current.CancellationToken);

        await _repoMock.DidNotReceive().TryParkRunAwaitingResumeAsync(1, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "Cleanup"));
        Assert.Equal(TaskExecutionStatus.Assigned, cleanup.Status);
    }

    /// <summary>The expiry sweep re-opened the run with its deadline past: this pass ends it (cleanup,
    /// then the RolledBack status its steps earned) instead of parking it again.</summary>
    [Fact]
    public async Task R39_AdvanceStageAsync_AfterTheResumeWindowElapsed_CleansUpInsteadOfWaitingAgain()
    {
        var (_, cleanup) = ArrangeRollbackOfAMissedConfirmation(
            rollbackSucceeded: true, resumeDeadline: DateTime.UtcNow.AddMinutes(-1));

        await _sut.AdvanceStageAsync(1, "Rollback", ct: TestContext.Current.CancellationToken);

        await _repoMock.DidNotReceive().TryParkRunAwaitingResumeAsync(1, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "Cleanup"));
        Assert.Equal(TaskExecutionStatus.Assigned, cleanup.Status);
    }

    [Fact]
    public async Task R39_ResumeAwaitingRunAsync_GoesThroughTheControlService_WithTheEngineScheduler()
    {
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.AwaitingResume,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { Id = 1, Name = "release", YamlDefinition = ConfirmedBlueGreenYaml }
        };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.ReopenAwaitingRunAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                run.Status = PipelineStatus.Running;
                return ResumeReopenOutcome.Reopened;
            });
        // The replayed stages are Pending again; no runner is online, so the pass dispatches nothing yet.
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new PipelineStepRun { Id = 2, PipelineRunId = 1, StageName = "Up", StepName = "start" },
            new PipelineStepRun { Id = 3, PipelineRunId = 1, StageName = "Confirm", StepName = "confirmed" },
            new PipelineStepRun { Id = 4, PipelineRunId = 1, StageName = "Rollback", StepName = "restore" }
        ]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>()).Returns([PipelineRunHelpers.SystemPrepareStage]);
        _repoMock.GetTerminalStageNamesAsync(1, Arg.Any<CancellationToken>()).Returns([PipelineRunHelpers.SystemPrepareStage]);

        await _sut.ResumeAwaitingRunAsync(1, "alice", TestContext.Current.CancellationToken);

        await _repoMock.Received(1).ReopenAwaitingRunAsync(
            1, Arg.Is<IReadOnlyCollection<string>>(stages => stages.SequenceEqual(new[] { "Up", "Confirm", "Rollback" })),
            Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("Resumed", "PipelineRun", 1, Arg.Any<string>(), Arg.Any<CancellationToken>());
        // The engine scheduler ran a dispatch pass over the replayed stages.
        await _repoMock.Received().GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_FailureHandlerAlreadyRunning_DoesNotDispatchSystemCleanup()
    {
        var yaml = """
            name: qa
            trigger: manual
            stages:
              - name: audit
                agent: linux-01
                steps:
                  - name: inspect
                    shell: exit 1
              - name: teardown
                agent: linux-01
                condition: always()
                depends_on: [audit]
                steps:
                  - name: destroy
                    shell: docker compose down
            """;
        var cleanup = new PipelineStepRun
        {
            Id = 3,
            PipelineRunId = 1,
            StageName = PipelineRunHelpers.SystemCleanupStage,
            StepName = "Cleanup",
            Status = TaskExecutionStatus.Pending,
            IsSystem = true
        };
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { YamlDefinition = yaml }
        };

        _repoMock.AreAllStepsInStageCompletedAsync(1, "audit", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "audit", Arg.Any<CancellationToken>()).Returns(
            [new PipelineStepRun { Id = 1, StageName = "audit", StepName = "inspect", Status = TaskExecutionStatus.Timeout }]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([cleanup]);
        _repoMock.HasAnyRunningStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        await _sut.AdvanceStageAsync(1, "audit", ct: TestContext.Current.CancellationToken);

        _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(task => task.Name == "Cleanup"));
        Assert.Equal(TaskExecutionStatus.Pending, cleanup.Status);
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_AfterFailureHandler_FinalizesOriginalFailure()
    {
        var yaml = """
            name: qa
            trigger: manual
            stages:
              - name: teardown
                agent: linux-01
                condition: always()
                steps:
                  - name: destroy
                    shell: docker compose down
            """;
        _repoMock.AreAllStepsInStageCompletedAsync(1, "teardown", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "teardown", Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(
            new PipelineRun { Id = 1, PipelineId = 1, Pipeline = new Pipeline { YamlDefinition = yaml } });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        await _sut.AdvanceStageAsync(1, "teardown", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Success, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_HasPendingSteps_CreatesTasksForNextStage()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: deploy
                agent: linux-01
                execution_role: deploy
                steps:
                  - name: deploy-app
                    shell: deploy.sh
            """;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun { Id = 2, StageName = "deploy", StepName = "deploy-app", PipelineRunId = 1 }]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01", PipelineRunnerEnabled = true });
        ServerTask? dispatched = null;
        _repoMock.TrackTask(Arg.Do<ServerTask>(task => dispatched = task));
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(dispatched);
        Assert.Equal("deploy-app", dispatched.Name);
        Assert.Equal(10, dispatched.ServerId);
        var dispatchedEnvironment = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            dispatched.EnvironmentVariables)!;
        Assert.Equal("deploy", dispatchedEnvironment["AETHEUS_EXECUTION_ROLE"]);
        await _repoMock.DidNotReceive().FindOnlineDeployTargetAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(Arg.Any<int>(), Arg.Any<PipelineStatus>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_ArtifactCollectionUsesSuccessfulStageProducer()
    {
        const string yaml = """
            name: artifacts
            trigger: manual
            stages:
              - name: build
                isolation:
                  mode: container
                  toolchain: dotnet
                artifacts:
                  - out/**
                steps:
                  - name: compile
                    shell: dotnet build
            """;
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { Id = 1, Name = "artifacts", YamlDefinition = yaml }
        };
        var producer = new Server { Id = 22, Name = "producer", OrganizationId = 10, OsType = OsType.Linux };
        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPipelineOrganizationIdAsync(1, Arg.Any<CancellationToken>()).Returns(10);
        _repoMock.GetStageProducerServerIdAsync(1, "build", Arg.Any<CancellationToken>()).Returns(22);
        _repoMock.FindOnlineServerByIdAsync(22, OsType.Unknown, Arg.Any<CancellationToken>()).Returns(producer);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task =>
            task.ServerId == 22
            && task.Operation == OperationKind.PipelineCollectArtifacts
            && task.EnvironmentVariables.Contains("\"AETHEUS_WORKING_DIR\":\"/w\"", StringComparison.Ordinal)
            && task.EnvironmentVariables.Contains("\"AETHEUS_WORKSPACE_MODE\":\"container\"", StringComparison.Ordinal)));
        await _repoMock.DidNotReceive().FindAnyOnlineRunnerAsync(
            Arg.Any<int?>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_ArtifactProducerOffline_FailsRunInsteadOfSkippingCollection()
    {
        // BACK-019: the producer went offline between its success and the collection. Advancing let the
        // consumer run and the run end Success without the artifact it declared.
        const string yaml = """
            name: artifacts
            trigger: manual
            stages:
              - name: build
                artifacts:
                  - out/**
                steps:
                  - name: compile
                    shell: dotnet build
              - name: deploy
                depends_on: [build]
                steps:
                  - name: ship
                    shell: ./ship.sh
            """;
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { Id = 1, Name = "artifacts", YamlDefinition = yaml }
        };
        var consumer = new PipelineStepRun
        {
            Id = 5,
            PipelineRunId = 1,
            StageName = "deploy",
            StepName = "ship",
            Status = TaskExecutionStatus.Pending
        };
        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPipelineOrganizationIdAsync(1, Arg.Any<CancellationToken>()).Returns(10);
        _repoMock.GetStageProducerServerIdAsync(1, "build", Arg.Any<CancellationToken>()).Returns(22);
        _repoMock.FindOnlineServerByIdAsync(22, OsType.Unknown, Arg.Any<CancellationToken>()).Returns((Server?)null);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => consumer.Status == TaskExecutionStatus.Pending ? [consumer] : []);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task =>
            task.ServerId == 22
            && task.Operation == OperationKind.PipelineCollectArtifacts
            && task.Status == TaskExecutionStatus.Failed
            && task.FailureReason!.Contains("cannot be collected", StringComparison.Ordinal)));
        _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(task => task.Name == "ship"));
        Assert.Equal(TaskExecutionStatus.Cancelled, consumer.Status);
        await _repoMock.Received(1).AppendRunWarningsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(warnings =>
                warnings.Single().Contains("producer runner (server 22)", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Success, Arg.Any<CancellationToken>());
    }

    // --- "Run to end" (PLAN-002 step D): one run tests everything, a failure still fails it ---

    private const string RunToEndYaml = """
        name: ci
        trigger: manual
        stages:
          - name: tests
            agent: linux-01
            steps:
              - name: integration
                shell: dotnet test
              - name: flaky-lint
                shell: lint.sh
                continue_on_error: true
        """;

    private async Task<(PipelineRun Run, List<PipelineStepRun> Steps)> LaunchRunToEndYamlAsync(bool runToEnd)
    {
        PipelineRun? added = null;
        var steps = new List<PipelineStepRun>();
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "ci", YamlDefinition = RunToEndYaml, Runs = [] });
        _repoMock.When(x => x.AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()))
            .Do(call => added = call.Arg<PipelineRun>());
        _repoMock.When(x => x.TrackPipelineStepRun(Arg.Any<PipelineStepRun>()))
            .Do(call => steps.Add(call.Arg<PipelineStepRun>()));
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 1, PipelineId = 1, Status = PipelineStatus.Running, Pipeline = new Pipeline { Name = "ci" }, StepRuns = [] });

        var preparation = await _sut.PrepareRunAsync(1, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(preparation);
        await _sut.TriggerPreparedRunAsync(preparation with { RunToEnd = runToEnd }, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(added);
        return (added, steps);
    }

    [Fact]
    public async Task TriggerPreparedRunAsync_RunToEnd_EveryStepContinuesOnError_DeclaredOnesKeepTheirMeaning()
    {
        var (run, steps) = await LaunchRunToEndYamlAsync(runToEnd: true);

        Assert.True(run.RunToEnd);
        var integration = steps.Single(step => step.StepName == "integration");
        Assert.True(integration.ContinueOnError);
        Assert.True(integration.ContinueOnErrorForced);
        // continue_on_error declared by the step: the same as in a normal run, never "forced".
        var lint = steps.Single(step => step.StepName == "flaky-lint");
        Assert.True(lint.ContinueOnError);
        Assert.False(lint.ContinueOnErrorForced);
        // The workspace clone stays blocking: without it nothing can run.
        Assert.False(steps.Single(step => step.StageName == PipelineRunHelpers.SystemPrepareStage).ContinueOnError);
    }

    [Fact]
    public async Task TriggerPreparedRunAsync_WithoutRunToEnd_StepsKeepTheirDeclaredBehaviour()
    {
        var (run, steps) = await LaunchRunToEndYamlAsync(runToEnd: false);

        Assert.False(run.RunToEnd);
        Assert.False(steps.Single(step => step.StepName == "integration").ContinueOnError);
        Assert.All(steps, step => Assert.False(step.ContinueOnErrorForced));
    }

    private void ArrangeRunToEndStageDone(params PipelineStepRun[] failed)
    {
        _repoMock.AreAllStepsInStageCompletedAsync(1, "tests", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "tests", Arg.Any<CancellationToken>()).Returns([.. failed]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            RunToEnd = true,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { Id = 1, YamlDefinition = RunToEndYaml }
        });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.GetContinuableFailedStepsInRunAsync(1, Arg.Any<CancellationToken>()).Returns([.. failed]);
    }

    private static PipelineStepRun FailedRunToEndStep(string name, bool forced) => new()
    {
        Id = forced ? 1 : 2,
        PipelineRunId = 1,
        StageName = "tests",
        StepName = name,
        Status = TaskExecutionStatus.Failed,
        ContinueOnError = true,
        ContinueOnErrorForced = forced
    };

    [Fact]
    public async Task AdvanceStageAsync_RunToEnd_AStepThatWentOnOnlyByTheMode_EndsTheRunFailed()
    {
        ArrangeRunToEndStageDone(FailedRunToEndStep("integration", forced: true));

        await _sut.AdvanceStageAsync(1, "tests", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Partial, Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Success, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_RunToEnd_OnlyADeclaredContinueOnErrorFailed_StaysPartial()
    {
        ArrangeRunToEndStageDone(FailedRunToEndStep("flaky-lint", forced: false));

        await _sut.AdvanceStageAsync(1, "tests", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Partial, Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HasRunToEndFailureAsync_SeesOnlyFailuresTheModeLetThrough()
    {
        _repoMock.GetContinuableFailedStepsInRunAsync(1, Arg.Any<CancellationToken>())
            .Returns([FailedRunToEndStep("flaky-lint", forced: false)]);
        Assert.False(await _sut.HasRunToEndFailureAsync(1, TestContext.Current.CancellationToken));

        _repoMock.GetContinuableFailedStepsInRunAsync(1, Arg.Any<CancellationToken>())
            .Returns([FailedRunToEndStep("flaky-lint", forced: false), FailedRunToEndStep("integration", forced: true)]);
        Assert.True(await _sut.HasRunToEndFailureAsync(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AdvanceStageAsync_RunToEnd_UncollectableArtifact_FailsTheRunButLetsTheNextStageRun()
    {
        // AdvanceStageAsync_ArtifactProducerOffline_FailsRunInsteadOfSkippingCollection, in "run to end":
        // the missing artifact is recorded and keeps the run from Success, but the next stage is
        // dispatched instead of cancelled; a step that needs the artifact fails by itself.
        const string yaml = """
            name: artifacts
            trigger: manual
            stages:
              - name: build
                artifacts:
                  - out/**
                steps:
                  - name: compile
                    shell: dotnet build
              - name: security
                agent: linux-01
                depends_on: [build]
                steps:
                  - name: scan
                    shell: ./scan.sh
            """;
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            RunToEnd = true,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { Id = 1, Name = "artifacts", YamlDefinition = yaml }
        };
        var scan = new PipelineStepRun
        {
            Id = 5,
            PipelineRunId = 1,
            StageName = "security",
            StepName = "scan",
            Status = TaskExecutionStatus.Pending,
            ContinueOnError = true,
            ContinueOnErrorForced = true
        };
        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetStageProducerServerIdAsync(1, "build", Arg.Any<CancellationToken>()).Returns(22);
        _repoMock.FindOnlineServerByIdAsync(22, OsType.Unknown, Arg.Any<CancellationToken>()).Returns((Server?)null);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => scan.Status == TaskExecutionStatus.Pending ? [scan] : []);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>()).Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.GetTerminalStageNamesAsync(1, Arg.Any<CancellationToken>()).Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.HasFailedArtifactCollectionAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.NotEqual(TaskExecutionStatus.Cancelled, scan.Status);
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "scan"));
        await _repoMock.Received(1).AppendRunWarningsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(warnings =>
                warnings.Single().Contains("producer runner (server 22)", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Success, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceToNextStageOrCompleteAsync_FailedArtifactCollection_NeverFinalizesSuccess()
    {
        // BACK-019: once the workspace cleanup reports back, nothing is pending and no step failed; the
        // failed collection alone must still decide the outcome.
        const string yaml = "name: artifacts\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: dotnet build";
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(
            new PipelineRun { Id = 1, PipelineId = 7, Pipeline = new Pipeline { Id = 7, YamlDefinition = yaml } });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.HasFailedArtifactCollectionAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        await _sut.ContinueAfterArtifactCollectionAsync(1, TaskExecutionStatus.Success, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Success, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContinueAfterArtifactCollectionAsync_Success_CompletesRunSuccessfully()
    {
        const string yaml = "name: artifacts\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: dotnet build";
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(
            new PipelineRun
            {
                Id = 1,
                PipelineId = 7,
                Pipeline = new Pipeline { Id = 7, YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        await _sut.ContinueAfterArtifactCollectionAsync(1, TaskExecutionStatus.Success, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Success, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(TaskExecutionStatus.Failed)]
    [InlineData(TaskExecutionStatus.Cancelled)]
    [InlineData(TaskExecutionStatus.Timeout)]
    public async Task ContinueAfterArtifactCollectionAsync_NonSuccess_FailsRunClosed(
        TaskExecutionStatus collectionStatus)
    {
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([]);

        await _sut.ContinueAfterArtifactCollectionAsync(1, collectionStatus, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AppendRunWarningsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(warnings =>
                warnings.Single().Contains(collectionStatus.ToString(), StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        // Nothing is dispatched past a collection that failed (the run has no failure handler).
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task ContinueAfterArtifactCollectionAsync_NonSuccess_WaitsForSystemCleanupBeforeFinalizing()
    {
        const string yaml = "name: artifacts\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: dotnet build";
        var cleanup = new PipelineStepRun
        {
            Id = 2,
            PipelineRunId = 1,
            StageName = PipelineRunHelpers.SystemCleanupStage,
            StepName = "Cleanup",
            Status = TaskExecutionStatus.Pending,
            IsSystem = true
        };
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([cleanup]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(
            new PipelineRun
            {
                Id = 1,
                PipelineId = 7,
                Status = PipelineStatus.Running,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { Id = 7, YamlDefinition = yaml }
            });
        _repoMock.GetRunAffinityServerIdAsync(1, Arg.Any<CancellationToken>()).Returns(10);
        _repoMock.FindOnlineServerByIdAsync(10, OsType.Unknown, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01", OsType = OsType.Linux });

        await _sut.ContinueAfterArtifactCollectionAsync(
            1, TaskExecutionStatus.Failed, TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "Cleanup"));
        Assert.Equal(TaskExecutionStatus.Assigned, cleanup.Status);
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    // R-38: a vault the run needs vanished after it started. Resolving the variables for the next stage refuses
    // (BadRequestException), which used to escape the scheduler: the run stayed Running with Pending steps, and the
    // recovery sweep re-drove it every minute, logging the same Critical until the one-hour stuck-run backstop. The
    // refusal now stops the run with its reason, as the variable guard intends ("must stop the run").
    private const string MissingVaultYaml = """
        name: vault-gone
        trigger: manual
        vaults:
          - gone-vault
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: compile
                shell: dotnet build
        """;

    private PipelineStepRun ArrangeRunWhoseNextStageIsRefused(PipelineStepRun pending)
    {
        _vaultMock.ResolveVaultSecretsWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string>(), new HashSet<string>()));
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([pending]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(
            new PipelineRun
            {
                Id = 1,
                PipelineId = 7,
                Status = PipelineStatus.Running,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { Id = 7, YamlDefinition = MissingVaultYaml }
            });
        return pending;
    }

    [Fact]
    public async Task ReconcileRunAsync_WhenTheNextStageIsRefused_StopsTheRunWithTheReasonInsteadOfThrowing()
    {
        var build = ArrangeRunWhoseNextStageIsRefused(new PipelineStepRun
        {
            Id = 3,
            PipelineRunId = 1,
            StageName = "build",
            StepName = "compile",
            Status = TaskExecutionStatus.Pending
        });

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AppendRunWarningsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(warnings =>
                warnings.Single().Contains("gone-vault", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        Assert.Equal(TaskExecutionStatus.Failed, build.Status);
        Assert.Contains("gone-vault", build.FailureReason, StringComparison.Ordinal);
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task AdvanceStageAsync_WhenTheNextStageIsRefused_StopsTheRunSoTheCompletingTaskIsNotRejected()
    {
        var build = ArrangeRunWhoseNextStageIsRefused(new PipelineStepRun
        {
            Id = 3,
            PipelineRunId = 1,
            StageName = "build",
            StepName = "compile",
            Status = TaskExecutionStatus.Pending
        });
        _repoMock.AreAllStepsInStageCompletedAsync(1, "prepare", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "prepare", Arg.Any<CancellationToken>()).Returns([]);

        await _sut.AdvanceStageAsync(1, "prepare", TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, build.Status);
        Assert.Contains("gone-vault", build.FailureReason, StringComparison.Ordinal);
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileRunAsync_WhenTheNextStageIsRefused_CancelsTheCleanupItCannotRunAndSaysSo()
    {
        var cleanup = new PipelineStepRun
        {
            Id = 4,
            PipelineRunId = 1,
            StageName = PipelineRunHelpers.SystemCleanupStage,
            StepName = "Cleanup",
            Status = TaskExecutionStatus.Pending,
            IsSystem = true
        };
        var build = new PipelineStepRun
        {
            Id = 3,
            PipelineRunId = 1,
            StageName = "build",
            StepName = "compile",
            Status = TaskExecutionStatus.Pending
        };
        ArrangeRunWhoseNextStageIsRefused(build);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([build, cleanup]);

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, build.Status);
        Assert.Contains("gone-vault", build.FailureReason, StringComparison.Ordinal);
        // The cleanup resolves the same variables: it would be refused too, so it is cancelled, not left Pending.
        Assert.Equal(TaskExecutionStatus.Cancelled, cleanup.Status);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
        await _repoMock.Received(1).AppendRunWarningsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(warnings =>
                warnings.Single().Contains("cleanup was not run", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileRunAsync_WhenTheNextStageIsRefusedWhileAnotherStepRuns_LeavesTheRunToThatStep()
    {
        var build = ArrangeRunWhoseNextStageIsRefused(new PipelineStepRun
        {
            Id = 3,
            PipelineRunId = 1,
            StageName = "build",
            StepName = "compile",
            Status = TaskExecutionStatus.Pending
        });
        _repoMock.HasAnyRunningStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Pending, build.Status);
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileRunAsync_AfterTheCleanupOfARefusedRun_FinalizesFailedFromItsFailedSteps()
    {
        // The pass that follows the cleanup completion: no step is pending, the refused steps are Failed.
        // The variables are still refused, but nothing is left to dispatch, so the run just finalizes.
        ArrangeRunWhoseNextStageIsRefused(new PipelineStepRun { Id = 3, PipelineRunId = 1, StageName = "build", StepName = "compile" });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Success, Arg.Any<CancellationToken>());
    }

    // R-38 (follow-up): the refusal of the first R-38 fix came from resolving the run's variables. The same
    // BadRequestException is raised from every other place a scheduling pass can refuse the run's
    // configuration while it creates a stage's tasks: a configuration template the renderer refuses, a
    // repository or configuration read refused, the variables of the cleanup, of a resume or of an artifact
    // collection. Each used to escape the scheduler the same way: the run stayed Running with Pending steps,
    // the recovery sweep re-drove it every minute and logged a Critical each time. Every one now stops the run.
    private const string ExposeApacheProxyYaml = """
        name: expose
        trigger: manual
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: compile
                shell: dotnet build
          - name: expose
            agent: linux-01
            steps:
              - name: proxy
                type: apache-proxy
                server_name: app.example.com
                upstream: http://127.0.0.1:8080
        """;

    private const string ExposeApacheConfigYaml = """
        name: expose
        trigger: manual
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: compile
                shell: dotnet build
          - name: expose
            agent: linux-01
            steps:
              - name: notify
                shell: echo exposing
              - name: config
                type: apache-config
                config_files:
                  "app.example.com.conf": ".pipeline/configs/apache/site.conf"
        """;

    private static PipelineStepRun ExposeStep(int id, string name) => new()
    {
        Id = id,
        PipelineRunId = 1,
        StageName = "expose",
        StepName = name,
        Status = TaskExecutionStatus.Pending
    };

    private void ArrangeRunningStage(string yaml, string variablesJson, params PipelineStepRun[] steps)
        => ArrangeAdvanceToStageWithVariables(yaml, "expose", variablesJson, PipelineStatus.Running, steps);
    private async Task AssertTheRunWasStoppedAsync(string reasonFragment, params PipelineStepRun[] failedSteps)
    {
        foreach (var step in failedSteps)
        {
            Assert.Equal(TaskExecutionStatus.Failed, step.Status);
            Assert.Contains(reasonFragment, step.FailureReason, StringComparison.Ordinal);
        }
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        await _repoMock.Received().AppendRunWarningsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(warnings =>
                warnings.Any(warning => warning.Contains(reasonFragment, StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileRunAsync_WhenAnApacheProxyTemplateIsRefusedByTheRenderer_StopsTheRunInsteadOfThrowing()
    {
        var proxy = ExposeStep(2, "proxy");
        ArrangeRunningStage(ExposeApacheProxyYaml, "{}", proxy);
        _pipelineGitMock.ReadProjectConfigAtRevisionAsync(
                7, ".pipeline/configs/apache/reverse-proxy-http.conf", DefaultCommit, Arg.Any<CancellationToken>(), 11)
            .Returns("ServerName #{NOT_DEFINED}#\n");

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        await AssertTheRunWasStoppedAsync("'NOT_DEFINED' is not defined", proxy);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Theory]
    [InlineData("ServerName #{NOT_DEFINED}#\n", "'NOT_DEFINED' is not defined")]
    [InlineData("ServerName #{WITH_BREAK}#\n", "forbidden line break")]
    [InlineData("ServerName #{ SPACED }#\n", "invalid or unresolved token")]
    [InlineData("OVERSIZE", "safety limit")]
    public async Task ReconcileRunAsync_WhenAnApacheConfigTemplateIsRefusedByTheRenderer_StopsTheRunInsteadOfThrowing(
        string template, string reasonFragment)
    {
        var config = ExposeStep(3, "config");
        ArrangeRunningStage(ExposeApacheConfigYaml, "{\"WITH_BREAK\":\"a\\nb\"}", config);
        _pipelineGitMock.ReadProjectConfigAtRevisionAsync(
                7, ".pipeline/configs/apache/site.conf", DefaultCommit, Arg.Any<CancellationToken>(), 11)
            .Returns(template == "OVERSIZE" ? new string('x', 256 * 1024 + 1) : template);

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        await AssertTheRunWasStoppedAsync(reasonFragment, config);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task ReconcileRunAsync_WhenTheTemplateReadIsRefused_StopsTheRunInsteadOfThrowing()
    {
        // The read refuses (several repositories in the project, a source repository detached since the run
        // started...) with the same BadRequestException the renderer raises.
        var proxy = ExposeStep(2, "proxy");
        ArrangeRunningStage(ExposeApacheProxyYaml, "{}", proxy);
        _pipelineGitMock.ReadProjectConfigAtRevisionAsync(
                7, ".pipeline/configs/apache/reverse-proxy-http.conf", DefaultCommit, Arg.Any<CancellationToken>(), 11)
            .Returns(Task.FromException<string?>(new BadRequestException(
                "Project 7 has multiple repositories; select an explicit pipeline source repository.")));

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        await AssertTheRunWasStoppedAsync("multiple repositories", proxy);
    }

    [Fact]
    public async Task AdvanceStageAsync_WhenTheTemplateReadIsRefused_StopsTheRunSoTheCompletingTaskIsNotRejected()
    {
        var notify = ExposeStep(2, "notify");
        var config = ExposeStep(3, "config");
        ArrangeRunningStage(ExposeApacheConfigYaml, "{}", notify, config);
        _pipelineGitMock.ReadProjectConfigAtRevisionAsync(
                7, ".pipeline/configs/apache/site.conf", DefaultCommit, Arg.Any<CancellationToken>(), 11)
            .Returns(Task.FromException<string?>(new BadRequestException(
                "Source repository 11 does not belong to project 7.")));

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        await AssertTheRunWasStoppedAsync("does not belong to project 7", config);
    }

    [Fact]
    public async Task ReconcileRunAsync_WhenALaterStepOfTheStageIsRefused_FailsTheWholeStageAndKeepsNoTaskOfIt()
    {
        // 'notify' is dispatched first (its task is tracked), then 'config' is refused: nothing of the stage
        // may stay behind, neither the tracked task nor the half-dispatched step.
        var notify = ExposeStep(2, "notify");
        var config = ExposeStep(3, "config");
        ArrangeRunningStage(ExposeApacheConfigYaml, "{}", notify, config);
        _pipelineGitMock.ReadProjectConfigAtRevisionAsync(
                7, ".pipeline/configs/apache/site.conf", DefaultCommit, Arg.Any<CancellationToken>(), 11)
            .Returns("ServerName #{NOT_DEFINED}#\n");

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "notify"));
        _repoMock.Received(1).DiscardUnsavedInsertsSince(Arg.Any<UnsavedInsertsMark>());
        await AssertTheRunWasStoppedAsync("'NOT_DEFINED' is not defined", notify, config);
    }

    [Fact]
    public async Task ReconcileRunAsync_WhenALaterStepOfTheStageIsRefused_SavesNoTaskOfTheStage()
    {
        // The unit of work behind the mocked repository is a real one (EF in-memory), so what the stop saves is
        // what the database would receive: the task of 'notify', tracked before 'config' was refused, must
        // not be in it, and the step must not keep the link to it.
        var options = new DbContextOptionsBuilder<Aetheus.Back.Data.AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new Aetheus.Back.Data.AppDbContext(options);
        var unitOfWork = new PipelineRepository(
            db, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineRepository>.Instance,
            new Aetheus.Back.Components.Tasks.PipelineTaskLifecycleRepository(db, TimeProvider.System),
            new PipelineRunLineageRepository(db));
        var notify = ExposeStep(0, "notify");
        var config = ExposeStep(0, "config");
        db.PipelineStepRuns.AddRange(notify, config);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        ArrangeRunningStage(ExposeApacheConfigYaml, "{}", notify, config);
        _repoMock.When(repository => repository.TrackTask(Arg.Any<ServerTask>()))
            .Do(call => unitOfWork.TrackTask(call.Arg<ServerTask>()));
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => unitOfWork.SaveChangesAsync(call.Arg<CancellationToken>()));
        _repoMock.MarkUnsavedInserts().Returns(_ => unitOfWork.MarkUnsavedInserts());
        _repoMock.When(repository => repository.DiscardUnsavedInsertsSince(Arg.Any<UnsavedInsertsMark>()))
            .Do(call => unitOfWork.DiscardUnsavedInsertsSince(call.Arg<UnsavedInsertsMark>()));
        _pipelineGitMock.ReadProjectConfigAtRevisionAsync(
                7, ".pipeline/configs/apache/site.conf", DefaultCommit, Arg.Any<CancellationToken>(), 11)
            .Returns("ServerName #{NOT_DEFINED}#\n");

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        Assert.Empty(await db.Tasks.ToListAsync(TestContext.Current.CancellationToken));
        var saved = await db.PipelineStepRuns.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, saved.Count);
        Assert.All(saved, step =>
        {
            Assert.Equal(TaskExecutionStatus.Failed, step.Status);
            Assert.Null(step.TaskId);
        });
    }

    private const string ExposeGateWithoutStatusesYaml = """
        name: expose
        trigger: manual
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: compile
                shell: dotnet build
          - name: expose
            agent: linux-01
            steps:
              - name: notify
                shell: echo exposing
              - name: gate
                type: gate-status
        """;

    [Fact]
    public async Task ReconcileRunAsync_WhenALaterStepFactoryRefusesItsStep_SavesNoTaskOfTheStage()
    {
        // Sibling of the test above for a refusal that is not a BadRequestException: a step factory (here
        // gate-status without any status variable) returns its reason, and the run is failed by
        // FailRunOnFactoryErrorAsync. The task of 'notify', tracked before 'gate' was refused, must not be
        // saved by the failing run's own save.
        var options = new DbContextOptionsBuilder<Aetheus.Back.Data.AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new Aetheus.Back.Data.AppDbContext(options);
        var unitOfWork = new PipelineRepository(
            db, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineRepository>.Instance,
            new Aetheus.Back.Components.Tasks.PipelineTaskLifecycleRepository(db, TimeProvider.System),
            new PipelineRunLineageRepository(db));
        var notify = ExposeStep(0, "notify");
        var gate = ExposeStep(0, "gate");
        db.PipelineStepRuns.AddRange(notify, gate);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        ArrangeRunningStage(ExposeGateWithoutStatusesYaml, "{}", notify, gate);
        _repoMock.When(repository => repository.TrackTask(Arg.Any<ServerTask>()))
            .Do(call => unitOfWork.TrackTask(call.Arg<ServerTask>()));
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => unitOfWork.SaveChangesAsync(call.Arg<CancellationToken>()));
        _repoMock.MarkUnsavedInserts().Returns(_ => unitOfWork.MarkUnsavedInserts());
        _repoMock.When(repository => repository.DiscardUnsavedInsertsSince(Arg.Any<UnsavedInsertsMark>()))
            .Do(call => unitOfWork.DiscardUnsavedInsertsSince(call.Arg<UnsavedInsertsMark>()));

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "notify"));
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        Assert.Empty(await db.Tasks.ToListAsync(TestContext.Current.CancellationToken));
        var saved = await db.PipelineStepRuns.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, saved.Count);
        Assert.All(saved, step =>
        {
            Assert.Equal(TaskExecutionStatus.Failed, step.Status);
            Assert.Null(step.TaskId);
        });
    }

    private const string ExposeAiAfterAShellStepYaml = """
        name: expose
        trigger: manual
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: compile
                shell: dotnet build
          - name: expose
            agent: linux-01
            steps:
              - name: notify
                shell: echo exposing
              - name: review
                type: ai
                profile: claude-sonnet
                prompt: Review the commits.
        """;

    private const string ExposeMatrixYaml = """
        name: expose
        trigger: manual
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: compile
                shell: dotnet build
          - name: expose
            matrix:
              os: [linux, windows]
            steps:
              - name: pkg
                shell: echo pkg
        """;

    /// <summary>A real unit of work (EF in-memory) behind the mocked repository, so what a failing run saves is
    /// what the database would receive. The steps are persisted first, as a launched run has them. Call it AFTER ArrangeRunningStage, which stubs SaveChangesAsync itself.</summary>
    private async Task<Aetheus.Back.Data.AppDbContext> ArrangeRealUnitOfWorkAsync(params PipelineStepRun[] steps)
    {
        var options = new DbContextOptionsBuilder<Aetheus.Back.Data.AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new Aetheus.Back.Data.AppDbContext(options);
        var unitOfWork = new PipelineRepository(
            db, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PipelineRepository>.Instance,
            new Aetheus.Back.Components.Tasks.PipelineTaskLifecycleRepository(db, TimeProvider.System),
            new PipelineRunLineageRepository(db));
        db.PipelineStepRuns.AddRange(steps);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _repoMock.When(repository => repository.TrackTask(Arg.Any<ServerTask>()))
            .Do(call => unitOfWork.TrackTask(call.Arg<ServerTask>()));
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => unitOfWork.SaveChangesAsync(call.Arg<CancellationToken>()));
        _repoMock.MarkUnsavedInserts().Returns(_ => unitOfWork.MarkUnsavedInserts());
        _repoMock.When(repository => repository.DiscardUnsavedInsertsSince(Arg.Any<UnsavedInsertsMark>()))
            .Do(call => unitOfWork.DiscardUnsavedInsertsSince(call.Arg<UnsavedInsertsMark>()));
        _repoMock.CancelActiveTasksOfStepRunsAsync(
                Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => unitOfWork.CancelActiveTasksOfStepRunsAsync(
                call.ArgAt<int>(0), call.ArgAt<IReadOnlyCollection<int>>(1), call.ArgAt<CancellationToken>(2)));
        return db;
    }

    private static async Task AssertNoTaskWasSavedAndTheStepsFailedAsync(
        Aetheus.Back.Data.AppDbContext db, int expectedStepCount)
    {
        Assert.Empty(await db.Tasks.ToListAsync(TestContext.Current.CancellationToken));
        var saved = await db.PipelineStepRuns.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expectedStepCount, saved.Count);
        Assert.All(saved, step =>
        {
            Assert.Equal(TaskExecutionStatus.Failed, step.Status);
            Assert.Null(step.TaskId);
        });
    }

    [Fact]
    public async Task ReconcileRunAsync_WhenALaterStepIsANonAdvisoryAiStepWithNoProfile_SavesNoTaskOfTheStage()
    {
        // Same defect class as the factory refusal: 'notify' is tracked, then the AI step is refused (the
        // pipeline belongs to no organization, so no AI runner profile can be resolved) and the run is failed
        // by CreateAiTaskAsync. The task of 'notify' must not be saved by that failing save.
        var notify = ExposeStep(0, "notify");
        var review = ExposeStep(0, "review");
        ArrangeRunningStage(ExposeAiAfterAShellStepYaml, "{}", notify, review);
        await using var db = await ArrangeRealUnitOfWorkAsync(notify, review);
        _repoMock.GetPipelineOrganizationIdAsync(1, Arg.Any<CancellationToken>()).Returns((int?)null);

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "notify"));
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        await AssertNoTaskWasSavedAndTheStepsFailedAsync(db, 2);
    }

    [Fact]
    public async Task ReconcileRunAsync_WhenALaterMatrixLegHasNoRunner_SavesNoTaskOfTheStage()
    {
        // The linux leg is dispatched (its task is tracked), then the windows leg finds no online runner and
        // the run is failed from ResolveAndValidateLegAsync's error. Nothing of the stage may be saved, and
        // the steps of the leg already dispatched fail with the stage, not only those of the refused leg.
        var linux = ExposeStep(0, "pkg [linux]");
        linux.MatrixLeg = "linux";
        var windows = ExposeStep(0, "pkg [windows]");
        windows.MatrixLeg = "windows";
        ArrangeRunningStage(ExposeMatrixYaml, "{}", linux, windows);
        await using var db = await ArrangeRealUnitOfWorkAsync(linux, windows);
        _repoMock.FindAnyOnlineRunnerAsync(Arg.Any<int?>(), OsType.Windows, Arg.Any<CancellationToken>())
            .Returns((Server?)null);

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "pkg [linux]"));
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        await AssertNoTaskWasSavedAndTheStepsFailedAsync(db, 2);
    }

    private const string TwoIndependentStagesSecondRefusedYaml = """
        name: expose
        trigger: manual
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: compile
                shell: dotnet build
          - name: alpha
            agent: linux-01
            steps:
              - name: notify
                shell: echo alpha
          - name: beta
            agent: linux-01
            steps:
              - name: gate
                type: gate-status
        """;

    [Fact]
    public async Task ReconcileRunAsync_WhenASecondIndependentStageOfThePassIsRefused_SavesNoTaskOfTheFirstStage()
    {
        // Two stages without depends_on are ready in the same pass. 'alpha' is valid and its task is queued;
        // 'beta' is refused (gate-status without any status variable). The run is failed, so the task of
        // 'alpha' must not be written by the failing save: an agent could otherwise execute a task of a
        // failed run. Both stages' steps fail with the reason and none keeps a link to a task.
        var notify = ExposeStep(0, "notify");
        notify.StageName = "alpha";
        var gate = ExposeStep(0, "gate");
        gate.StageName = "beta";
        ArrangeRunningStage(TwoIndependentStagesSecondRefusedYaml, "{}", notify, gate);
        await using var db = await ArrangeRealUnitOfWorkAsync(notify, gate);

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "notify"));
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        await AssertNoTaskWasSavedAndTheStepsFailedAsync(db, 2);
    }

    private const string ThreeIndependentStagesMiddleSkippedLastRefusedYaml = """
        name: expose
        trigger: manual
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: compile
                shell: dotnet build
          - name: alpha
            agent: linux-01
            steps:
              - name: notify
                shell: echo alpha
          - name: beta
            agent: linux-01
            condition: cancelled()
            steps:
              - name: skipped
                shell: echo beta
          - name: gamma
            agent: linux-01
            steps:
              - name: gate
                type: gate-status
        """;

    /// <summary>Every task of the database that an agent could still pick up or keep running.</summary>
    private static async Task<List<ServerTask>> ExecutableTasksAsync(Aetheus.Back.Data.AppDbContext db) =>
        (await db.Tasks.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken))
            .Where(task => task.Status is TaskExecutionStatus.Pending or TaskExecutionStatus.Assigned
                or TaskExecutionStatus.Running)
            .ToList();

    [Fact]
    public async Task ReconcileRunAsync_WhenAStageSkippedByItsConditionSavesMidPassAndALaterStageIsRefused_LeavesNoExecutableTask()
    {
        // 'alpha' is valid and its task is queued. 'beta' is skipped for its false condition, which saves
        // mid-pass and so writes alpha's queued task. 'gamma' is then refused (gate-status without status
        // variables): the discard of the unsaved inserts can no longer reach alpha's task, which is already
        // in the database for a run that is failed. No agent may be able to execute it.
        var notify = ExposeStep(0, "notify");
        notify.StageName = "alpha";
        var skipped = ExposeStep(0, "skipped");
        skipped.StageName = "beta";
        var gate = ExposeStep(0, "gate");
        gate.StageName = "gamma";
        ArrangeRunningStage(ThreeIndependentStagesMiddleSkippedLastRefusedYaml, "{}", notify, skipped, gate);
        await using var db = await ArrangeRealUnitOfWorkAsync(notify, skipped, gate);

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "notify"));
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        Assert.Empty(await ExecutableTasksAsync(db));
        var saved = (await db.PipelineStepRuns.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken))
            .ToDictionary(step => step.StepName);
        Assert.Equal(TaskExecutionStatus.Failed, saved["notify"].Status);
        Assert.Equal(TaskExecutionStatus.Failed, saved["gate"].Status);
        Assert.Equal(TaskExecutionStatus.Cancelled, saved["skipped"].Status);
        Assert.NotNull(saved["skipped"].SkippedCondition);
    }

    private const string ValidEnvironmentStageThenRefusedStageYaml = """
        name: expose
        trigger: manual
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: compile
                shell: dotnet build
          - name: alpha
            agent: linux-01
            steps:
              - name: notify
                shell: echo alpha
          - name: beta
            environment: prod
            steps:
              - name: publish
                shell: echo beta
          - name: gamma
            agent: linux-01
            steps:
              - name: gate
                type: gate-status
        """;

    [Fact]
    public async Task ReconcileRunAsync_WhenAnEnvironmentCheckFailureSavesMidPassAndALaterStageIsRefused_LeavesNoExecutableTask()
    {
        // The failed required check of 'beta' saves its Failed steps mid-pass, which writes the task queued
        // for 'alpha'; 'gamma' is then refused. The saved task of 'alpha' must be cancelled with the run.
        var notify = ExposeStep(0, "notify");
        notify.StageName = "alpha";
        var publish = ExposeStep(0, "publish");
        publish.StageName = "beta";
        var gate = ExposeStep(0, "gate");
        gate.StageName = "gamma";
        ArrangeRunningStage(ValidEnvironmentStageThenRefusedStageYaml, "{}", notify, publish, gate);
        await using var db = await ArrangeRealUnitOfWorkAsync(notify, publish, gate);
        ArrangeEnvironment(new EnvironmentCheck
        {
            Name = "unsupported",
            Type = EnvironmentCheckType.TemplateRequirement,
            IsRequired = true
        });

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        Assert.Empty(await ExecutableTasksAsync(db));
        var saved = (await db.PipelineStepRuns.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken))
            .ToDictionary(step => step.StepName);
        Assert.Equal(TaskExecutionStatus.Failed, saved["notify"].Status);
        Assert.Equal(TaskExecutionStatus.Failed, saved["publish"].Status);
        Assert.Equal(TaskExecutionStatus.Failed, saved["gate"].Status);
    }

    [Fact]
    public async Task ReconcileRunAsync_WhenAnApprovalSavesMidPassAndALaterStageIsRefused_LeavesNoExecutableTask()
    {
        // The approval row of 'beta' is added through the shared unit of work, which saves and so writes the
        // task queued for 'alpha'. The run was no longer Running (the transition is refused), so the pass
        // goes on, dispatches 'beta' too, and 'gamma' is refused. Neither saved task may stay executable.
        var notify = ExposeStep(0, "notify");
        notify.StageName = "alpha";
        var publish = ExposeStep(0, "publish");
        publish.StageName = "beta";
        var gate = ExposeStep(0, "gate");
        gate.StageName = "gamma";
        ArrangeRunningStage(ValidEnvironmentStageThenRefusedStageYaml, "{}", notify, publish, gate);
        await using var db = await ArrangeRealUnitOfWorkAsync(notify, publish, gate);
        ArrangeEnvironment();
        _repoMock.FindEnvironmentByNameAsync("prod", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Aetheus.Back.Data.Entities.Environment { Id = 6, Name = "prod", RequireApproval = true });
        _repoMock.GetApprovalsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.AddApprovalAsync(Arg.Any<PipelineApproval>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                db.PipelineApprovals.Add(call.Arg<PipelineApproval>());
                await db.SaveChangesAsync(call.Arg<CancellationToken>());
            });

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        Assert.Single(await db.PipelineApprovals.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "notify"));
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        Assert.Empty(await ExecutableTasksAsync(db));
        var saved = (await db.PipelineStepRuns.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken))
            .ToDictionary(step => step.StepName);
        Assert.Equal(TaskExecutionStatus.Failed, saved["notify"].Status);
        Assert.Equal(TaskExecutionStatus.Failed, saved["publish"].Status);
        Assert.Equal(TaskExecutionStatus.Failed, saved["gate"].Status);
    }

    [Fact]
    public async Task ReconcileRunAsync_WhenATemplateIsRefusedAndTheCleanupCanRun_PurgesTheWorkspaceBeforeEndingTheRun()
    {
        // The refusal does not come from the variables, so the cleanup is not refused: it is dispatched, and its
        // completion finalizes the run from the failed step (the run is not closed under it).
        var config = ExposeStep(3, "config");
        var cleanup = new PipelineStepRun
        {
            Id = 4,
            PipelineRunId = 1,
            StageName = PipelineRunHelpers.SystemCleanupStage,
            StepName = "Cleanup",
            Status = TaskExecutionStatus.Pending,
            IsSystem = true
        };
        ArrangeRunningStage(ExposeApacheConfigYaml, "{}", config);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([config, cleanup]);
        _repoMock.GetRunAffinityServerIdAsync(1, Arg.Any<CancellationToken>()).Returns(10);
        _repoMock.FindOnlineServerByIdAsync(10, OsType.Unknown, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01", OsType = OsType.Linux });
        _pipelineGitMock.ReadProjectConfigAtRevisionAsync(
                7, ".pipeline/configs/apache/site.conf", DefaultCommit, Arg.Any<CancellationToken>(), 11)
            .Returns("ServerName #{NOT_DEFINED}#\n");

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, config.Status);
        Assert.Equal(TaskExecutionStatus.Assigned, cleanup.Status);
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "Cleanup"));
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().AppendRunWarningsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(warnings =>
                warnings.Any(warning => warning.Contains("cleanup was not run", StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task ReconcileRunAsync_WhenTheEnvTemplateReadIsRefused_StopsTheRunInsteadOfThrowing()
    {
        const string yaml = """
            name: expose
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: expose
                agent: linux-01
                steps:
                  - name: start
                    shell: ./start.sh
                    env_template: .pipeline/configs/app.env
            """;
        var start = ExposeStep(2, "start");
        ArrangeRunningStage(yaml, "{}", start);
        var configRepository = Substitute.For<IProjectConfigRepositoryService>();
        configRepository.ReadAsync(
                7, ".pipeline/configs/app.env", Arg.Any<string?>(), 11, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new BadRequestException(
                "Project 7 has multiple repositories; select an explicit pipeline source repository.")));
        _envFiles = new PipelineEnvFilePreparer(_repoMock, configRepository, _secretMaskingMock);
        var sut = BuildSutWithOwnCollaborators();

        await sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        await AssertTheRunWasStoppedAsync("multiple repositories", start);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task ResumeAfterApprovalAsync_WhenTheRunsConfigurationIsRefused_StopsTheRunInsteadOfThrowing()
    {
        var build = ArrangeRunWhoseNextStageIsRefused(new PipelineStepRun
        {
            Id = 3,
            PipelineRunId = 1,
            StageName = "build",
            StepName = "compile",
            Status = TaskExecutionStatus.Pending
        });
        // Read waiting for the approval first, then running again once the resume flipped it.
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(
            new PipelineRun
            {
                Id = 1,
                PipelineId = 7,
                Status = PipelineStatus.WaitingForApproval,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { Id = 7, YamlDefinition = MissingVaultYaml }
            },
            new PipelineRun
            {
                Id = 1,
                PipelineId = 7,
                Status = PipelineStatus.Running,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { Id = 7, YamlDefinition = MissingVaultYaml }
            });
        _repoMock.TryTransitionPipelineRunStatusAsync(
                1, PipelineStatus.WaitingForApproval, PipelineStatus.Running, Arg.Any<CancellationToken>())
            .Returns(true);

        await _sut.ResumeAfterApprovalAsync(1, ct: TestContext.Current.CancellationToken);

        await AssertTheRunWasStoppedAsync("gone-vault", build);
    }

    [Fact]
    public async Task AdvanceStageAsync_WhenTheCleanupOfAFailedRunIsRefused_StopsTheRunInsteadOfThrowing()
    {
        var cleanup = new PipelineStepRun
        {
            Id = 4,
            PipelineRunId = 1,
            StageName = PipelineRunHelpers.SystemCleanupStage,
            StepName = "Cleanup",
            Status = TaskExecutionStatus.Pending,
            IsSystem = true
        };
        ArrangeRunWhoseNextStageIsRefused(new PipelineStepRun { Id = 3, PipelineRunId = 1, StageName = "build", StepName = "compile" });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([cleanup]);
        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>()).Returns(
            [new PipelineStepRun { Id = 3, StageName = "build", StepName = "compile", Status = TaskExecutionStatus.Failed }]);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        // The cleanup resolves the variables the run is refused: it is cancelled, and the warning says so.
        Assert.Equal(TaskExecutionStatus.Cancelled, cleanup.Status);
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        await _repoMock.Received().AppendRunWarningsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(warnings =>
                warnings.Any(warning => warning.Contains("cleanup was not run", StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_WhenTheArtifactCollectionOfAStageIsRefused_StopsTheRunInsteadOfThrowing()
    {
        const string yaml = """
            name: artifacts
            trigger: manual
            vaults:
              - gone-vault
            stages:
              - name: build
                artifacts:
                  - out/**
                steps:
                  - name: compile
                    shell: dotnet build
            """;
        var producer = new Server { Id = 22, Name = "producer", OrganizationId = 10, OsType = OsType.Linux };
        ArrangeRunWhoseNextStageIsRefused(new PipelineStepRun { Id = 3, PipelineRunId = 1, StageName = "build", StepName = "compile" });
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(
            new PipelineRun
            {
                Id = 1,
                PipelineId = 7,
                Status = PipelineStatus.Running,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { Id = 7, Name = "artifacts", YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineOrganizationIdAsync(7, Arg.Any<CancellationToken>()).Returns(10);
        _repoMock.GetStageProducerServerIdAsync(1, "build", Arg.Any<CancellationToken>()).Returns(22);
        _repoMock.FindOnlineServerByIdAsync(22, OsType.Unknown, Arg.Any<CancellationToken>()).Returns(producer);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        await AssertTheRunWasStoppedAsync("gone-vault");
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task AdvanceStageAsync_Dispatch_FlipsStepRunToAssigned_GuardsAgainstDoubleDispatch()
    {
        // 0-c regression: a dispatched step-run must leave Pending the instant its task is tracked.
        // Otherwise an orchestration tick (sibling completion / TaskTimeoutService) fired before the
        // agent claims the task re-selects the still-Pending step via GetPendingStepRunsAsync (which
        // filters Status == Pending) and dispatches a SECOND task - duplicate Create Release /
        // Collect Artifacts, wasted agent time. Asserting Assigned proves the real query excludes it.
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: deploy
                agent: linux-01
                steps:
                  - name: deploy-app
                    shell: deploy.sh
            """;

        var pendingStep = new PipelineStepRun
        {
            Id = 2,
            StageName = "deploy",
            StepName = "deploy-app",
            PipelineRunId = 1,
            Status = TaskExecutionStatus.Pending
        };

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([pendingStep]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });
        _repoMock.TrackTask(Arg.Any<ServerTask>());
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Assigned, pendingStep.Status);
        Assert.NotNull(pendingStep.Task);
    }

    [Fact]
    public async Task AdvanceStageAsync_ExplicitTargetKeepsEligibleRunAffinity()
    {
        const string yaml = """
            name: fast-release
            trigger: manual
            stages:
              - name: build
                environment: prod
                steps:
                  - name: build-images
                    shell: docker build .
              - name: deploy
                environment: prod
                depends_on:
                  - build
                steps:
                  - name: deploy-images
                    shell: deploy.sh
            """;
        var deployStep = new PipelineStepRun
        {
            Id = 2,
            PipelineRunId = 1,
            StageName = "deploy",
            StepName = "deploy-images",
            Status = TaskExecutionStatus.Pending
        };
        ArrangeAdvanceToStage(yaml, "deploy", deployStep);
        ArrangeEnvironment();
        var affinityRunner = new Server { Id = 10, Name = "prod-01", OsType = OsType.Linux };
        _repoMock.GetRunAffinityServerIdAsync(1, Arg.Any<CancellationToken>()).Returns(10);
        _repoMock.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), false, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([10, 20]);
        _repoMock.FindOnlineServerByIdAsync(10, OsType.Unknown, Arg.Any<CancellationToken>())
            .Returns(affinityRunner);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task =>
            task.ServerId == 10 && task.PipelineStepRunId == deployStep.Id));
        await _repoMock.Received(1).FindCandidateTargetServerIdsAsync(
            null, "prod", "", OsType.Unknown, Arg.Any<int?>(), false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().FindOnlineServerInEnvironmentAsync(
            "prod", Arg.Any<int?>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_EligibleAffinityRunnerOffline_DoesNotRerouteWorkspaceStage()
    {
        const string yaml = """
            name: fast-release
            trigger: manual
            stages:
              - name: build
                environment: prod
                steps:
                  - name: build-images
                    shell: docker build .
              - name: deploy
                environment: prod
                depends_on:
                  - build
                steps:
                  - name: deploy-images
                    shell: deploy.sh
            """;
        var deployStep = new PipelineStepRun
        {
            Id = 2,
            PipelineRunId = 1,
            StageName = "deploy",
            StepName = "deploy-images",
            Status = TaskExecutionStatus.Pending
        };
        ArrangeAdvanceToStage(yaml, "deploy", deployStep);
        ArrangeEnvironment();
        _repoMock.GetRunAffinityServerIdAsync(1, Arg.Any<CancellationToken>()).Returns(10);
        _repoMock.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), false, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([10, 20]);
        _repoMock.FindOnlineServerByIdAsync(10, OsType.Unknown, Arg.Any<CancellationToken>())
            .Returns((Server?)null);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Pending, deployStep.Status);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
        await _repoMock.Received(1).FindCandidateTargetServerIdsAsync(
            null, "prod", "", OsType.Unknown, Arg.Any<int?>(), false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().FindOnlineServerInEnvironmentAsync(
            "prod", Arg.Any<int?>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_ExplicitTargetThatExcludesAffinity_ResolvesRequestedRunner()
    {
        const string yaml = """
            name: cross-target
            trigger: manual
            stages:
              - name: build
                environment: build
                steps:
                  - name: compile
                    shell: dotnet build
              - name: deploy
                environment: prod
                depends_on:
                  - build
                steps:
                  - name: publish
                    shell: publish.sh
            """;
        var deployStep = new PipelineStepRun
        {
            Id = 2,
            PipelineRunId = 1,
            StageName = "deploy",
            StepName = "publish",
            Status = TaskExecutionStatus.Pending
        };
        ArrangeAdvanceToStage(yaml, "deploy", deployStep);
        ArrangeEnvironment();
        var requestedRunner = new Server { Id = 20, Name = "prod-02", OsType = OsType.Linux };
        _repoMock.GetRunAffinityServerIdAsync(1, Arg.Any<CancellationToken>()).Returns(10);
        _repoMock.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), false, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([20]);
        _repoMock.FindOnlineServerInEnvironmentAsync("prod", Arg.Any<int?>(), OsType.Unknown, Arg.Any<CancellationToken>())
            .Returns(requestedRunner);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task =>
            task.ServerId == 20 && task.PipelineStepRunId == deployStep.Id));
        await _repoMock.Received(1).FindCandidateTargetServerIdsAsync(
            null, "prod", "", OsType.Unknown, Arg.Any<int?>(), false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().FindOnlineServerByIdAsync(
            10, Arg.Any<OsType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_MaxParallelOne_DispatchesOnlyOneJobOfGroup()
    {
        // S-TECH-56: a stage group with strategy.max_parallel=1 must dispatch one job at a time.
        var yaml = """
            name: fanout
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: fanout
                agent: linux-01
                depends_on: [build]
                strategy:
                  max_parallel: 1
                jobs:
                  - name: jobA
                    steps:
                      - name: a
                        shell: echo a
                  - name: jobB
                    steps:
                      - name: b
                        shell: echo b
                  - name: jobC
                    steps:
                      - name: c
                        shell: echo c
            """;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns(
            [
                new PipelineStepRun { Id = 2, StageName = "jobA", StepName = "a", PipelineRunId = 1 },
                new PipelineStepRun { Id = 3, StageName = "jobB", StepName = "b", PipelineRunId = 1 },
                new PipelineStepRun { Id = 4, StageName = "jobC", StepName = "c", PipelineRunId = 1 }
            ]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.GetActiveStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });
        _repoMock.TrackTask(Arg.Any<ServerTask>());
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        // Only one of the three jobs is dispatched; the run is NOT failed by the throttle.
        _repoMock.Received(1).TrackTask(Arg.Any<ServerTask>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_MaxParallelOne_JobInFlight_ThrottlesWithoutFailing()
    {
        // One job already in flight + max_parallel=1 ⇒ no new dispatch, run stays alive (not failed).
        var yaml = """
            name: fanout
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: fanout
                agent: linux-01
                depends_on: [build]
                strategy:
                  max_parallel: 1
                jobs:
                  - name: jobA
                    steps:
                      - name: a
                        shell: echo a
                  - name: jobB
                    steps:
                      - name: b
                        shell: echo b
            """;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Running,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun { Id = 3, StageName = "jobB", StepName = "b", PipelineRunId = 1 }]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        // jobA is currently running - fills the single max_parallel slot.
        _repoMock.GetActiveStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns(["jobA"]);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_ReleaseStep_DispatchesCreateReleaseOperation()
    {
        // S-TECH-40: the backend leg of the `type: release` step - it must dispatch a
        // PipelineCreateRelease operation carrying the resolved version and project/run context.
        // (The agent-side execution of that operation still requires a live deployment to verify.)
        var yaml = """
            name: ship
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: ship
                agent: linux-01
                steps:
                  - name: tag-release
                    type: release
                    version: "2.5.0"
                    changelog: true
                    deployed: true
                    artifact: app-package
                    artifact_source_pipeline: ci
            """;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                CommitHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                BranchName = "main",
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { Id = 1, ProjectId = 5, YamlDefinition = yaml }
            });
        var sourcePipeline = new Pipeline { Id = 2, ProjectId = 5, Name = "ci", YamlDefinition = "name: ci\nstages: []" };
        _repoMock.FindTriggeredRunIdByPipelineNameAsync(1, "ci", Arg.Any<CancellationToken>()).Returns(8);
        _repoMock.GetPipelineRunWithPipelineAsync(8, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 8,
            PipelineId = 2,
            CommitHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Status = PipelineStatus.Success,
            Pipeline = sourcePipeline
        });
        _artifactRepoMock.FindRunArtifactByNameAsync(8, "app-package", Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifact
            {
                Id = 77,
                PipelineRunId = 8,
                ProjectId = 5,
                Name = "app-package",
                Sha256 = new string('a', 64)
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun { Id = 2, StageName = "ship", StepName = "tag-release", PipelineRunId = 1 }]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.GetActiveStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineProjectIdAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>())
            .Returns(5);
        _repoMock.GetProjectReleasePatternAsync(5, Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });
        ServerTask? trackedTask = null;
        _repoMock.When(repository => repository.TrackTask(Arg.Any<ServerTask>()))
            .Do(call => trackedTask = call.Arg<ServerTask>());
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(trackedTask);
        Assert.Equal("tag-release", trackedTask.Name);
        Assert.Equal(OperationKind.PipelineCreateRelease, trackedTask.Operation);
        Assert.Equal("2.5.0", trackedTask.Command);
        Assert.Equal(10, trackedTask.ServerId);
        var releaseEnvironment = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            trackedTask.EnvironmentVariables);
        Assert.NotNull(releaseEnvironment);
        Assert.Equal("8", releaseEnvironment["AETHEUS_RELEASE_ARTIFACT_RUN_ID"]);
        Assert.Equal("true", releaseEnvironment["AETHEUS_RELEASE_DEPLOYED"]);
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", releaseEnvironment["AETHEUS_RELEASE_COMMIT"]);
        Assert.Equal("main", releaseEnvironment["AETHEUS_RELEASE_BRANCH"]);
    }

    [Fact]
    public async Task AdvanceStageAsync_DeployRelease_SubstitutesRollbackSelectorBeforeResolvingArtifact()
    {
        var yaml = """
            name: rollback
            trigger: manual
            stages:
              - name: rollback
                agent: linux-01
                steps:
                  - name: redeploy
                    type: deploy
                    app: rollbacklocal
                    release: "$(ROLLBACK_RELEASE)"
                    health_url: http://127.0.0.1:18080/health/ready
            """;
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            AdditionalVariablesJson = "{\"ROLLBACK_RELEASE\":\"4\"}",
            Pipeline = new Pipeline { Id = 1, ProjectId = 7, YamlDefinition = yaml }
        };

        _repoMock.AreAllStepsInStageCompletedAsync(1, PipelineRunHelpers.SystemPrepareStage, Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, PipelineRunHelpers.SystemPrepareStage, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns(
            [new PipelineStepRun { Id = 2, StageName = "rollback", StepName = "redeploy", PipelineRunId = 1 }]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>()).Returns([PipelineRunHelpers.SystemPrepareStage]);
        _repoMock.GetActiveStageNamesAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineProjectIdAsync(run.Pipeline, Arg.Any<CancellationToken>()).Returns(7);
        _repoMock.FindOnlineDeployTargetAsync(null, null, "linux-01", Arg.Any<OsType>(), Arg.Is<int?>(organization => organization == null), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01", DeploymentTargetAvailable = true });
        _artifactRepoMock.FindReleaseArtifactSelectionAsync(
                7, "4", null, Arg.Any<CancellationToken>())
            .Returns(new ReleaseArtifactSelection(
                new PipelineArtifact { Id = 42, Name = "release", FilePath = "7/1/1/release.zip" }, 4));
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, PipelineRunHelpers.SystemPrepareStage, ct: TestContext.Current.CancellationToken);

        await _artifactRepoMock.Received(1).FindReleaseArtifactSelectionAsync(
            7, "4", null, Arg.Any<CancellationToken>());
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task =>
            task.Operation == OperationKind.PipelineDeploy
            && task.Command == "rollbacklocal"
            && task.EnvironmentVariables.Contains("\"AETHEUS_DEPLOY_ARTIFACT_ID\":\"42\"", StringComparison.Ordinal)
            && task.EnvironmentVariables.Contains("\"AETHEUS_DEPLOY_RELEASE_ID\":\"4\"", StringComparison.Ordinal)
            && task.EnvironmentVariables.Contains("AETHEUS_DEPLOY_HEALTH_URL", StringComparison.Ordinal)
            && task.EnvironmentVariables.Contains("health/ready", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AdvanceStageAsync_AStepTypeThisBackendDoesNotKnow_FailsByName_WithoutAnAgentTask()
    {
        // A type written for a newer backend validates with a warning; when it runs on this one it
        // fails visibly instead of reaching an agent as an empty command.
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: Future
                agent: linux-01
                steps:
                  - name: Future step
                    type: publish-to-the-moon
            """;
        var stepRun = new PipelineStepRun { Id = 2, StageName = "Future", StepName = "Future step", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "Future", stepRun);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, stepRun.Status);
        Assert.Contains("publish-to-the-moon", stepRun.FailureReason, StringComparison.Ordinal);
        _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(task => task.PipelineStepRunId == 2));
    }

    [Fact]
    public async Task AdvanceStageAsync_AdvanceBranchStep_RunsInTheBackend_WithoutAnAgentTask()
    {
        // The branch is moved by the backend, which hosts the repository; no agent task (and so no
        // git credential on an agent) is involved.
        var yaml = """
            name: deploy
            trigger: manual
            variables:
              AETHEUS_CANDIDATE_VERSION: "2.4.0-2483"
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: Advance main
                agent: linux-01
                steps:
                  - name: Advance main onto the deployed release
                    type: advance-branch
                    branch: main
            """;
        ArrangeAdvanceToStage(yaml, "Advance main",
            new PipelineStepRun { Id = 2, StageName = "Advance main", StepName = "Advance main onto the deployed release", PipelineRunId = 1 });

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        await _branchAdvanceStepMock.Received(1).ExecuteAsync(
            1,
            Arg.Is<PipelineStepRun>(step => step.Id == 2),
            Arg.Is<PipelineStepDefinition>(step => step.Type == "advance-branch" && step.Branch == "main"),
            Arg.Any<Dictionary<string, string>>(),
            Arg.Any<CancellationToken>());
        _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(task => task.PipelineStepRunId == 2));
    }

    [Fact]
    public async Task AdvanceStageAsync_ReportSteps_DispatchTypedOperationsWithBudgets()
    {
        var yaml = """
            name: quality
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: quality
                agent: linux-01
                steps:
                  - name: substitute-config
                    type: substitute
                    target_files: ["appsettings.json"]
                  - name: publish-coverage
                    type: coverage
                    target_files: ["coverage-merged/Cobertura.xml"]
                    min_coverage: 75
                  - name: publish-lint
                    type: lint
                    analysis_category: accessibility
                    target_files: ["reports/lint.sarif"]
                  - name: publish-complexity
                    type: complexity
                    max_complexity: 12
                  - name: publish-mutation
                    type: mutation
                  - name: publish-payload
                    type: artifacts
                    artifact_name: Payload-artifacts
                    target_files: ["out/$(BUILD_BUILDID)/**"]
            """;
        ArrangeAdvanceToStage(yaml, "quality",
            new PipelineStepRun { Id = 2, StageName = "quality", StepName = "substitute-config", PipelineRunId = 1 },
            new PipelineStepRun { Id = 3, StageName = "quality", StepName = "publish-coverage", PipelineRunId = 1 },
            new PipelineStepRun { Id = 4, StageName = "quality", StepName = "publish-lint", PipelineRunId = 1 },
            new PipelineStepRun { Id = 5, StageName = "quality", StepName = "publish-complexity", PipelineRunId = 1 },
            new PipelineStepRun { Id = 6, StageName = "quality", StepName = "publish-mutation", PipelineRunId = 1 },
            new PipelineStepRun { Id = 7, StageName = "quality", StepName = "publish-payload", PipelineRunId = 1 });

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t =>
            t.Name == "substitute-config" && t.Operation == OperationKind.PipelineSubstituteVariables
            && t.Command.Contains("appsettings.json", StringComparison.Ordinal)));
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t =>
            t.Name == "publish-coverage" && t.Operation == OperationKind.PipelinePublishCoverage
            && t.Command.Contains("Cobertura.xml", StringComparison.Ordinal)
            && t.EnvironmentVariables.Contains("AETHEUS_MIN_COVERAGE", StringComparison.Ordinal)
            && t.EnvironmentVariables.Contains("75", StringComparison.Ordinal)));
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t =>
            t.Name == "publish-lint" && t.Operation == OperationKind.PipelinePublishLint
            && t.Command.Contains("lint.sarif", StringComparison.Ordinal)
            && t.EnvironmentVariables.Contains("AETHEUS_LINT_CATEGORY", StringComparison.Ordinal)
            && t.EnvironmentVariables.Contains("Accessibility", StringComparison.Ordinal)));
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t =>
            t.Name == "publish-complexity" && t.Operation == OperationKind.PipelinePublishComplexity
            && t.EnvironmentVariables.Contains("AETHEUS_MAX_COMPLEXITY", StringComparison.Ordinal)
            && t.EnvironmentVariables.Contains("12", StringComparison.Ordinal)));
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t =>
            t.Name == "publish-mutation" && t.Operation == OperationKind.PipelinePublishMutation
            && t.Command.Contains("mutation-report.json", StringComparison.Ordinal)));
        // Step-level artifacts are collected under their own step name.
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t =>
            t.Name == "publish-payload" && t.Operation == OperationKind.PipelineCollectArtifacts
            && t.PipelineStepRunId == 7
            && !t.Command.Contains("$(BUILD_BUILDID)", StringComparison.Ordinal)
            && t.EnvironmentVariables.Contains("Payload-artifacts", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AdvanceStageAsync_ApacheAndCertbotSteps_DispatchValidatedTypedOperations()
    {
        var yaml = """
            name: expose
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: expose
                agent: linux-01
                steps:
                  - name: proxy
                    type: apache-proxy
                    server_name: app.example.com
                    upstream: http://127.0.0.1:8080
                  - name: tls
                    type: certbot
                    domains: "app.example.com, www.example.com"
                    email: admin@example.com
            """;
        ArrangeAdvanceToStage(yaml, "expose",
            new PipelineStepRun { Id = 2, StageName = "expose", StepName = "proxy", PipelineRunId = 1 },
            new PipelineStepRun { Id = 3, StageName = "expose", StepName = "tls", PipelineRunId = 1 });
        _pipelineGitMock.ReadProjectConfigAtRevisionAsync(
                7, ".pipeline/configs/apache/reverse-proxy-http.conf", DefaultCommit,
                Arg.Any<CancellationToken>(), 11)
            .Returns("ServerName #{AETHEUS_APACHE_SERVER_NAME}#\nProxyPass / #{AETHEUS_APACHE_UPSTREAM}#/\n");

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t =>
            t.Name == "proxy" && t.Operation == OperationKind.ApacheConfigureProxy
            && t.Command == "app.example.com.conf"
            && t.EnvironmentVariables.Contains("AETHEUS_APACHE_CONFIG_B64", StringComparison.Ordinal)
            && t.EnvironmentVariables.Contains(
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                    "ServerName app.example.com\nProxyPass / http://127.0.0.1:8080/\n")),
                StringComparison.Ordinal)));
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t =>
            t.Name == "tls" && t.Operation == OperationKind.CertbotObtain
            && t.Command == "app.example.com"
            && t.EnvironmentVariables.Contains("www.example.com", StringComparison.Ordinal)));
        await _pipelineGitMock.Received(1).ReadProjectConfigAtRevisionAsync(
            7, ".pipeline/configs/apache/reverse-proxy-http.conf", DefaultCommit,
            Arg.Any<CancellationToken>(), 11);
    }

    [Fact]
    public async Task R534_AdvanceStageAsync_ReadsAVersionedTemplate_AtTheDefinitionRevision_NotTheWorkspaceOne()
    {
        // The run's workspace comes from another repository (DefaultCommit is a commit of that one);
        // .pipeline/configs is part of the definition and is read where the definition was.
        const string definitionCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var yaml = """
            name: expose
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: expose
                agent: linux-01
                steps:
                  - name: proxy
                    type: apache-proxy
                    server_name: app.example.com
                    upstream: http://127.0.0.1:8080
            """;
        ArrangeAdvanceToStageWithVariables(yaml, "expose",
            $$"""{"AETHEUS_DEFINITION_COMMIT":"{{definitionCommit}}"}""",
            new PipelineStepRun { Id = 2, StageName = "expose", StepName = "proxy", PipelineRunId = 1 });
        _pipelineGitMock.ReadProjectConfigAtRevisionAsync(
                7, ".pipeline/configs/apache/reverse-proxy-http.conf", definitionCommit,
                Arg.Any<CancellationToken>(), 11)
            .Returns("ServerName #{AETHEUS_APACHE_SERVER_NAME}#\n");

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t =>
            t.Name == "proxy" && t.Operation == OperationKind.ApacheConfigureProxy));
        await _pipelineGitMock.DidNotReceive().ReadProjectConfigAtRevisionAsync(
            Arg.Any<int>(), Arg.Any<string>(), DefaultCommit, Arg.Any<CancellationToken>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task AdvanceStageAsync_ApacheProxyMissingVersionedTemplate_FailsWithoutDispatch()
    {
        var yaml = """
            name: expose
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: expose
                agent: linux-01
                steps:
                  - name: proxy
                    type: apache-proxy
                    server_name: app.example.com
                    upstream: http://127.0.0.1:8080
                    config_template: .pipeline/configs/apache/custom-proxy.conf
            """;
        var proxy = new PipelineStepRun
        {
            Id = 2,
            StageName = "expose",
            StepName = "proxy",
            PipelineRunId = 1
        };
        ArrangeAdvanceToStage(yaml, "expose", proxy);
        _pipelineGitMock.ReadProjectConfigAtRevisionAsync(
                7, ".pipeline/configs/apache/custom-proxy.conf", DefaultCommit,
                Arg.Any<CancellationToken>(), 11)
            .Returns((string?)null);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, proxy.Status);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
        await _repoMock.Received().AppendRunWarningsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(warnings => warnings.Any(warning =>
                warning.Contains("custom-proxy.conf", StringComparison.Ordinal)
                && warning.Contains("not found", StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_InvalidApacheAndCertbotInputs_FailSynchronouslyWithoutDispatch()
    {
        var yaml = """
            name: expose
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: expose
                agent: linux-01
                steps:
                  - name: proxy
                    type: apache-proxy
                    server_name: "bad/domain"
                    upstream: ftp://example.com
                  - name: tls
                    type: certbot
                    domains: "bad/domain"
                    email: not-an-email
            """;
        var proxy = new PipelineStepRun { Id = 2, StageName = "expose", StepName = "proxy", PipelineRunId = 1 };
        var tls = new PipelineStepRun { Id = 3, StageName = "expose", StepName = "tls", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "expose", proxy, tls);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, proxy.Status);
        Assert.Equal(TaskExecutionStatus.Failed, tls.Status);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
        await _repoMock.Received().AppendRunWarningsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(w => w.Any(x => x.Contains("Apache-proxy", StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
        await _repoMock.Received().AppendRunWarningsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(w => w.Any(x => x.Contains("Certbot", StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_DeployArtifact_DispatchesOnlyToDeploymentTarget()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: deploy
                agent: prod-01
                steps:
                  - name: deploy-app
                    type: deploy
                    app: web-api
                    artifact: package
                    compose: deploy/docker-compose.yml
                    health_timeout_seconds: 45
            """;
        ArrangeAdvanceToStage(yaml, "deploy",
            new PipelineStepRun { Id = 2, StageName = "deploy", StepName = "deploy-app", PipelineRunId = 1 });
        _artifactRepoMock.FindRunArtifactByNameAsync(1, "package", Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifact { Id = 42, PipelineRunId = 1, Name = "package" });

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t =>
            t.Name == "deploy-app" && t.ServerId == 10 && t.Operation == OperationKind.PipelineDeploy
            && t.Command == "web-api"
            && t.EnvironmentVariables.Contains("AETHEUS_DEPLOY_ARTIFACT_ID", StringComparison.Ordinal)
            && t.EnvironmentVariables.Contains("deploy/docker-compose.yml", StringComparison.Ordinal)
            && t.EnvironmentVariables.Contains("45", StringComparison.Ordinal)));
        // The deployed artifact is recorded as an input of the run.
        _repoMock.Received(1).TrackArtifactInput(Arg.Is<PipelineRunArtifactInput>(input =>
            input.PipelineRunId == 1 && input.StepName == "deploy-app" && input.Kind == ArtifactInputKind.Deploy
            && input.ArtifactId == 42 && input.ArtifactName == "package" && input.SourcePipelineRunId == 1
            && input.ReleaseId == null));
        await _repoMock.Received(1).FindOnlineDeployTargetAsync(
            Arg.Any<string?>(), Arg.Any<string?>(), "prod-01", Arg.Any<OsType>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_RestoreArtifacts_UsesSuccessfulSiblingRunFromDirectParent()
    {
        var yaml = """
            name: qa
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: restore
                agent: qa-01
                steps:
                  - name: restore-package
                    type: restore-artifacts
                    artifact: app-package
                    artifact_source_pipeline: ci
            """;
        var step = new PipelineStepRun { Id = 2, StageName = "restore", StepName = "restore-package", PipelineRunId = 1 };
        ArrangeAdvanceToStageWithVariables(yaml, "restore", "{\"UPSTREAM_RUN_ID\":\"9\"}", step);
        var currentPipeline = new Pipeline { Id = 1, ProjectId = 5, YamlDefinition = yaml };
        var sourcePipeline = new Pipeline { Id = 2, ProjectId = 5, YamlDefinition = "name: ci\nstages: []" };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                CommitHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                AdditionalVariablesJson = "{\"UPSTREAM_RUN_ID\":\"9\"}",
                ResolvedVariablesJson = "{\"UPSTREAM_RUN_ID\":\"9\"}",
                Pipeline = currentPipeline
            });
        _repoMock.GetPipelineRunWithPipelineAsync(8, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 8,
                PipelineId = 2,
                CommitHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Status = PipelineStatus.Success,
                Pipeline = sourcePipeline
            });
        _repoMock.FindTriggeredRunIdByPipelineNameAsync(9, "ci", Arg.Any<CancellationToken>()).Returns(8);
        _repoMock.GetPipelineProjectIdAsync(currentPipeline, Arg.Any<CancellationToken>()).Returns(5);
        _repoMock.GetPipelineProjectIdAsync(sourcePipeline, Arg.Any<CancellationToken>()).Returns(5);
        _artifactRepoMock.FindRunArtifactByNameAsync(8, "app-package", Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifact
            {
                Id = 77,
                PipelineRunId = 8,
                ProjectId = 5,
                Name = "app-package",
                Sha256 = new string('a', 64)
            });

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Assigned, step.Status);
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t =>
            t.Operation == OperationKind.PipelineRestoreArtifacts && t.Command == "app-package"
            && t.EnvironmentVariables.Contains("77", StringComparison.Ordinal)
            && t.EnvironmentVariables.Contains("AETHEUS_RESTORE_ARTIFACT_ID", StringComparison.Ordinal)));
        // The restored artifact, its digest and its producing run are recorded.
        _repoMock.Received(1).TrackArtifactInput(Arg.Is<PipelineRunArtifactInput>(input =>
            input.PipelineRunId == 1 && input.StepName == "restore-package" && input.Kind == ArtifactInputKind.Restore
            && input.ArtifactId == 77 && input.SourcePipelineRunId == 8 && input.Sha256 == new string('a', 64)));
    }

    [Fact]
    public async Task R2097_RestoreArtifacts_OptionalFailedProducer_CompletesAndSaysSo_WithoutARunWarning()
    {
        var yaml = """
            name: candidate
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: assurance
                agent: linux-01
                steps:
                  - name: restore-summary
                    type: restore-artifacts
                    artifact: analysis-summary-json
                    artifact_source_pipeline: quality
                    allow_missing: true
            """;
        var step = new PipelineStepRun
        {
            Id = 2,
            StageName = "assurance",
            StepName = "restore-summary",
            PipelineRunId = 1
        };
        ArrangeAdvanceToStageWithVariables(yaml, "assurance", "{\"UPSTREAM_RUN_ID\":\"9\"}", step);
        var currentPipeline = new Pipeline { Id = 1, ProjectId = 5, YamlDefinition = yaml };
        var sourcePipeline = new Pipeline { Id = 2, ProjectId = 5, YamlDefinition = "name: quality\nstages: []" };
        const string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            CommitHash = commit,
            AdditionalVariablesJson = "{\"UPSTREAM_RUN_ID\":\"9\"}",
            ResolvedVariablesJson = "{\"UPSTREAM_RUN_ID\":\"9\"}",
            Pipeline = currentPipeline
        });
        _repoMock.GetPipelineRunWithPipelineAsync(8, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 8,
            PipelineId = 2,
            CommitHash = commit,
            Status = PipelineStatus.Failed,
            Pipeline = sourcePipeline
        });
        _repoMock.FindTriggeredRunIdByPipelineNameAsync(9, "quality", Arg.Any<CancellationToken>()).Returns(8);
        _repoMock.GetPipelineProjectIdAsync(currentPipeline, Arg.Any<CancellationToken>()).Returns(5);
        _repoMock.GetPipelineProjectIdAsync(sourcePipeline, Arg.Any<CancellationToken>()).Returns(5);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Success, step.Status);
        _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(task =>
            task.Operation == OperationKind.PipelineRestoreArtifacts));
        // The step states it itself (its badge reads "Skipped (artifact missing)" with this reason)...
        Assert.StartsWith("Nothing to restore: the optional artifact 'analysis-summary-json' of quality does not exist yet",
            step.SkippedReason, StringComparison.Ordinal);
        // ...and the run carries no warning for an absence the step was declared to accept.
        await _repoMock.DidNotReceive().AppendRunWarningsAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_RestoreArtifacts_TopLevelFastRunUsesExactCommitArtifact()
    {
        const string commit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var yaml = """
            name: fast
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: checkout
                    shell: git status
              - name: restore
                agent: linux-01
                steps:
                  - name: restore-package
                    type: restore-artifacts
                    artifact: app-package
                    artifact_source_pipeline: ci
            """;
        var step = new PipelineStepRun { Id = 2, StageName = "restore", StepName = "restore-package", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "restore", step);
        var pipeline = new Pipeline { Id = 1, ProjectId = 5, Name = "fast", YamlDefinition = yaml };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            CommitHash = commit,
            ResolvedVariablesJson = "{}",
            Pipeline = pipeline
        });
        _repoMock.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(5);
        _artifactRepoMock.FindSuccessfulPipelineArtifactByCommitAsync(
                5, "ci", commit, "app-package", Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifact
            {
                Id = 88,
                PipelineRunId = 7,
                ProjectId = 5,
                Name = "app-package",
                Sha256 = new string('b', 64)
            });

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Assigned, step.Status);
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task =>
            task.Operation == OperationKind.PipelineRestoreArtifacts
            && task.EnvironmentVariables.Contains("88", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AdvanceStageAsync_RestoreArtifacts_LatestSuccessfulSelectorIgnoresTheCurrentCommit()
    {
        // A scheduled pipeline never runs on the commit being deployed, so the default same-commit
        // lookup resolves nothing and its evidence would silently never apply.
        const string commit = "dddddddddddddddddddddddddddddddddddddddd";
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: checkout
                    shell: git status
              - name: restore
                agent: linux-01
                steps:
                  - name: restore-nightly
                    type: restore-artifacts
                    artifact: NightlyEvidence-artifacts
                    artifact_source_pipeline: nightly
                    artifact_source_selector: latest-successful
            """;
        var step = new PipelineStepRun { Id = 2, StageName = "restore", StepName = "restore-nightly", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "restore", step);
        var pipeline = new Pipeline { Id = 1, ProjectId = 5, Name = "deploy", YamlDefinition = yaml };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            CommitHash = commit,
            ResolvedVariablesJson = "{}",
            Pipeline = pipeline
        });
        _repoMock.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(5);
        _artifactRepoMock.FindLatestSuccessfulPipelineArtifactAsync(
                5, "nightly", "NightlyEvidence-artifacts", Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifact
            {
                Id = 91,
                PipelineRunId = 30,
                ProjectId = 5,
                Name = "NightlyEvidence-artifacts",
                Sha256 = new string('d', 64)
            });

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Assigned, step.Status);
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task =>
            task.Operation == OperationKind.PipelineRestoreArtifacts
            && task.EnvironmentVariables.Contains("91", StringComparison.Ordinal)));
        await _artifactRepoMock.DidNotReceive().FindSuccessfulPipelineArtifactByCommitAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_RestoreArtifacts_UnknownSelectorIsRefused()
    {
        const string commit = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: checkout
                    shell: git status
              - name: restore
                agent: linux-01
                steps:
                  - name: restore-nightly
                    type: restore-artifacts
                    artifact: NightlyEvidence-artifacts
                    artifact_source_pipeline: nightly
                    artifact_source_selector: whatever-is-newest
            """;
        var step = new PipelineStepRun { Id = 2, StageName = "restore", StepName = "restore-nightly", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "restore", step);
        var pipeline = new Pipeline { Id = 1, ProjectId = 5, Name = "deploy", YamlDefinition = yaml };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            CommitHash = commit,
            ResolvedVariablesJson = "{}",
            Pipeline = pipeline
        });
        _repoMock.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(5);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        await _artifactRepoMock.DidNotReceive().FindLatestSuccessfulPipelineArtifactAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_RestoreArtifacts_ReleaseUsesRetainedArtifactAndSafeTargetDirectory()
    {
        var yaml = """
            name: rollback-qa
            trigger: manual
            stages:
              - name: restore
                agent: linux-01
                steps:
                  - name: restore-n-minus-one
                    type: restore-artifacts
                    release: previous-deployed
                    artifact: BuildArtifacts-artifacts
                    target_directory: .nminus1
            """;
        var step = new PipelineStepRun { Id = 2, StageName = "restore", StepName = "restore-n-minus-one", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "restore", step);
        var pipeline = new Pipeline { Id = 1, ProjectId = 5, Name = "rollback-qa", YamlDefinition = yaml };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            CommitHash = "cccccccccccccccccccccccccccccccccccccccc",
            ResolvedVariablesJson = "{}",
            Pipeline = pipeline
        });
        _repoMock.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(5);
        _artifactRepoMock.FindPreviousDeployedReleaseArtifactAsync(
                5, "cccccccccccccccccccccccccccccccccccccccc",
                "BuildArtifacts-artifacts", Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifact
            {
                Id = 99,
                PipelineRunId = 7,
                ProjectId = 5,
                Name = "BuildArtifacts-artifacts",
                Sha256 = new string('d', 64)
            });

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Assigned, step.Status);
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task =>
            task.Operation == OperationKind.PipelineRestoreArtifacts
            && task.EnvironmentVariables.Contains("99", StringComparison.Ordinal)
            && task.EnvironmentVariables.Contains("AETHEUS_RESTORE_RELEASE_SELECTOR", StringComparison.Ordinal)
            && task.EnvironmentVariables.Contains("previous-deployed", StringComparison.Ordinal)
            && task.EnvironmentVariables.Contains("AETHEUS_RESTORE_TARGET_DIR", StringComparison.Ordinal)
            && task.EnvironmentVariables.Contains(".nminus1", StringComparison.Ordinal)));
    }

    /// <summary>Run 2691: only an explicit <c>deployed_baseline: none</c> sends the opt-out to the
    /// agent; an absent key or <c>required</c> sends nothing, which the agent reads as fail-closed.</summary>
    [Theory]
    [InlineData("deployed_baseline: none", true)]
    [InlineData("deployed_baseline: required", false)]
    [InlineData("", false)]
    public async Task AdvanceStageAsync_RestoreArtifacts_PassesDeployedBaselineOptOutOnlyWhenRequested(
        string baselineLine, bool expectOptOut)
    {
        var yaml = $"""
            name: rollback-qa
            trigger: manual
            stages:
              - name: restore
                agent: linux-01
                steps:
                  - name: restore-n-minus-one
                    type: restore-artifacts
                    release: previous-deployed
                    artifact: AgentRelease-artifacts
                    target_directory: .nminus1
                    {baselineLine}
            """;
        var step = new PipelineStepRun { Id = 2, StageName = "restore", StepName = "restore-n-minus-one", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "restore", step);
        var pipeline = new Pipeline { Id = 1, ProjectId = 5, Name = "rollback-qa", YamlDefinition = yaml };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            CommitHash = "cccccccccccccccccccccccccccccccccccccccc",
            ResolvedVariablesJson = "{}",
            Pipeline = pipeline
        });
        _repoMock.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(5);
        _artifactRepoMock.FindPreviousDeployedReleaseArtifactAsync(
                5, "cccccccccccccccccccccccccccccccccccccccc",
                "AgentRelease-artifacts", Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifact
            {
                Id = 99,
                PipelineRunId = 7,
                ProjectId = 5,
                Name = "AgentRelease-artifacts",
                Sha256 = new string('d', 64)
            });
        ServerTask? tracked = null;
        _repoMock.When(repo => repo.TrackTask(Arg.Is<ServerTask>(task =>
                task.Operation == OperationKind.PipelineRestoreArtifacts)))
            .Do(call => tracked = call.Arg<ServerTask>());

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Assigned, step.Status);
        Assert.NotNull(tracked);
        var env = JsonSerializer.Deserialize<Dictionary<string, string>>(tracked.EnvironmentVariables!)!;
        if (expectOptOut)
            Assert.Equal(PipelineDeployedBaseline.None, env[PipelineDeployedBaseline.EnvironmentVariable]);
        else
            Assert.False(env.ContainsKey(PipelineDeployedBaseline.EnvironmentVariable));
    }

    [Theory]
    [InlineData("previous-deployed")]
    [InlineData("current-deployed")]
    public async Task AdvanceStageAsync_RestoreArtifacts_MissingDeployedArtifact_AllowsExplicitBootstrap(
        string releaseSelector)
    {
        var yaml = $$"""
            name: rollback-qa
            trigger: manual
            stages:
              - name: restore
                agent: linux-01
                steps:
                  - name: restore-n-minus-one
                    type: restore-artifacts
                    release: {{releaseSelector}}
                    artifact: QaRuntime-artifacts
                    target_directory: .nminus1
                    allow_missing: true
            """;
        var step = new PipelineStepRun { Id = 2, StageName = "restore", StepName = "restore-n-minus-one", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "restore", step);
        var pipeline = new Pipeline { Id = 1, ProjectId = 5, Name = "rollback-qa", YamlDefinition = yaml };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            CommitHash = "dddddddddddddddddddddddddddddddddddddddd",
            ResolvedVariablesJson = "{}",
            Pipeline = pipeline
        });
        _repoMock.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(5);
        _artifactRepoMock.FindPreviousDeployedReleaseArtifactAsync(
                5, "dddddddddddddddddddddddddddddddddddddddd", "QaRuntime-artifacts", Arg.Any<CancellationToken>())
            .Returns((PipelineArtifact?)null);
        _artifactRepoMock.FindReleaseArtifactAsync(
                5, "current-deployed", "QaRuntime-artifacts", Arg.Any<CancellationToken>())
            .Returns((PipelineArtifact?)null);
        _artifactRepoMock.RequiresPreviousDeployedArtifactAsync(
            5, "dddddddddddddddddddddddddddddddddddddddd", "QaRuntime-artifacts",
            Arg.Any<CancellationToken>()).Returns(false);
        _artifactRepoMock.HasDeployedRollbackContractReleaseAsync(
            5, Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.AppendRunWarningsAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Success, step.Status);
        _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(task =>
            task.Operation == OperationKind.PipelineRestoreArtifacts));
        await _repoMock.Received(1).AppendRunWarningsAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(warnings => warnings.Any(message => PipelineRunNotices.CodeOf(message) == PipelineRunNotices.BaselineAbsent)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("latest-published")]
    [InlineData("current-deployed")]
    public async Task AdvanceStageAsync_RestoreArtifacts_MissingArtifactAfterContractRelease_FailsClosed(
        string releaseSelector)
    {
        var yaml = $$"""
            name: rollback-qa
            trigger: manual
            stages:
              - name: restore
                agent: linux-01
                steps:
                  - name: restore-n-minus-one
                    type: restore-artifacts
                    release: {{releaseSelector}}
                    target_directory: .nminus1
                    allow_missing: true
            """;
        var step = new PipelineStepRun { Id = 2, StageName = "restore", StepName = "restore-n-minus-one", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "restore", step);
        var pipeline = new Pipeline { Id = 1, ProjectId = 5, Name = "rollback-qa", YamlDefinition = yaml };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            CommitHash = "ffffffffffffffffffffffffffffffffffffffff",
            ResolvedVariablesJson = "{}",
            Pipeline = pipeline
        });
        _repoMock.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(5);
        _artifactRepoMock.FindReleaseArtifactAsync(
                5, releaseSelector, null, Arg.Any<CancellationToken>())
            .Returns((PipelineArtifact?)null);
        _artifactRepoMock.HasPublishedRollbackContractReleaseAsync(5, Arg.Any<CancellationToken>()).Returns(true);
        _artifactRepoMock.HasDeployedRollbackContractReleaseAsync(5, Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.AppendRunWarningsAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(task =>
            task.Operation == OperationKind.PipelineRestoreArtifacts));
        await _repoMock.Received(1).AppendRunWarningsAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(warnings => warnings.Any(message => message.Contains("has no retained artifact", StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_RestoreArtifacts_PublishedMetadataWithoutRetainedArtifact_AllowsExplicitBootstrap()
    {
        var yaml = """
            name: rollback-qa
            trigger: manual
            stages:
              - name: restore
                agent: linux-01
                steps:
                  - name: restore-n-minus-one
                    type: restore-artifacts
                    release: latest-published
                    target_directory: .nminus1
                    allow_missing: true
            """;
        var step = new PipelineStepRun { Id = 2, StageName = "restore", StepName = "restore-n-minus-one", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "restore", step);
        var pipeline = new Pipeline { Id = 1, ProjectId = 5, Name = "rollback-qa", YamlDefinition = yaml };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            CommitHash = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            ResolvedVariablesJson = "{}",
            Pipeline = pipeline
        });
        _repoMock.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(5);
        _artifactRepoMock.FindReleaseArtifactAsync(
                5, "latest-published", null, Arg.Any<CancellationToken>())
            .Returns((PipelineArtifact?)null);
        _artifactRepoMock.HasPublishedRollbackContractReleaseAsync(5, Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.AppendRunWarningsAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Success, step.Status);
        // The step says it restored nothing, so it is not drawn as a green tick.
        Assert.Contains("explicit fallback", step.SkippedReason, StringComparison.Ordinal);
        _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(task =>
            task.Operation == OperationKind.PipelineRestoreArtifacts));
        await _repoMock.Received(1).AppendRunWarningsAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(warnings => warnings.Any(message => PipelineRunNotices.CodeOf(message) == PipelineRunNotices.BaselineAbsent)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("", "package", "invalid or missing 'app'")]
    [InlineData("web-api", "", "neither 'artifact'")]
    [InlineData("web-api", "missing", "could not resolve")]
    public async Task AdvanceStageAsync_InvalidDeployDefinitions_FailWithoutCreatingTask(
        string app, string artifact, string expectedWarning)
    {
        var yaml = $$"""
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: deploy
                agent: prod-01
                steps:
                  - name: deploy-app
                    type: deploy
                    app: "{{app}}"
                    artifact: "{{artifact}}"
            """;
        var step = new PipelineStepRun { Id = 2, StageName = "deploy", StepName = "deploy-app", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "deploy", step);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
        await _repoMock.Received().AppendRunWarningsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(warnings => warnings.Any(w => w.Contains(expectedWarning, StringComparison.OrdinalIgnoreCase))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_ProtectedEnvironment_CreatesApprovalAndPausesBeforeDispatch()
    {
        var yaml = EnvironmentStageYaml();
        ArrangeAdvanceToStage(yaml, "ship",
            new PipelineStepRun { Id = 2, StageName = "ship", StepName = "publish", PipelineRunId = 1 });
        _repoMock.FindEnvironmentByNameAsync("prod", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Aetheus.Back.Data.Entities.Environment { Id = 6, Name = "prod", RequireApproval = true });
        _repoMock.GetApprovalsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineIdForRunAsync(1, Arg.Any<CancellationToken>()).Returns(12);
        _repoMock.TryTransitionPipelineRunStatusAsync(
                1, PipelineStatus.Running, PipelineStatus.WaitingForApproval, Arg.Any<CancellationToken>())
            .Returns(true);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddApprovalAsync(Arg.Is<PipelineApproval>(approval =>
            approval.PipelineRunId == 1 && approval.StageName == "ship" && approval.EnvironmentId == 6),
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).TryTransitionPipelineRunStatusAsync(
            1, PipelineStatus.Running, PipelineStatus.WaitingForApproval, Arg.Any<CancellationToken>());
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task AdvanceStageAsync_ProtectedEnvironment_ClearsAWaitingReasonLeftByAnEarlierThrottle()
    {
        // TryTransitionPipelineRunStatusAsync commits its own raw SQL update, outside this pass's
        // SaveChanges. A reason written while the stage was still throttled must not sit on the row
        // through WaitingForApproval and resurface the instant approval resumes the run.
        var yaml = EnvironmentStageYaml();
        ArrangeAdvanceToStage(yaml, "ship",
            new PipelineStepRun { Id = 2, StageName = "ship", StepName = "publish", PipelineRunId = 1 });
        _repoMock.FindEnvironmentByNameAsync("prod", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Aetheus.Back.Data.Entities.Environment { Id = 6, Name = "prod", RequireApproval = true });
        _repoMock.GetApprovalsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineIdForRunAsync(1, Arg.Any<CancellationToken>()).Returns(12);
        _repoMock.TryTransitionPipelineRunStatusAsync(
                1, PipelineStatus.Running, PipelineStatus.WaitingForApproval, Arg.Any<CancellationToken>())
            .Returns(true);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).SetRunWaitingReasonAsync(1, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_OptionalUnsupportedEnvironmentChecks_DoNotBlockDispatch()
    {
        var yaml = EnvironmentStageYaml();
        var step = new PipelineStepRun { Id = 2, StageName = "ship", StepName = "publish", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "ship", step);
        ArrangeEnvironment(
            new EnvironmentCheck { Name = "template", Type = EnvironmentCheckType.TemplateRequirement, IsRequired = false },
            new EnvironmentCheck { Name = "status", Type = EnvironmentCheckType.StatusCheck, IsRequired = false },
            new EnvironmentCheck { Name = "optional-unsafe", Type = EnvironmentCheckType.RestCallback, IsRequired = false, Configuration = "{\"url\":\"http://127.0.0.1\"}" });

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Assigned, step.Status);
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "publish" && task.ServerId == 10));
    }

    [Fact]
    public async Task AdvanceStageAsync_NonLoopbackDeployHealthUrl_FailsWithoutCreatingTask()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: deploy
                agent: prod-01
                steps:
                  - name: deploy-app
                    type: deploy
                    app: web-api
                    artifact: package
                    health_url: http://10.0.0.10/health/ready
            """;
        var step = new PipelineStepRun { Id = 2, StageName = "deploy", StepName = "deploy-app", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "deploy", step);
        _artifactRepoMock.FindRunArtifactByNameAsync(1, "package", Arg.Any<CancellationToken>())
            .Returns(new PipelineArtifact { Id = 42, PipelineRunId = 1, Name = "package" });

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
        await _repoMock.Received().AppendRunWarningsAsync(1,
            Arg.Is<IReadOnlyCollection<string>>(warnings => warnings.Any(w =>
                w.Contains("loopback HTTP(S) URL", StringComparison.Ordinal))),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(EnvironmentCheckType.TemplateRequirement)]
    [InlineData(EnvironmentCheckType.StatusCheck)]
    public async Task AdvanceStageAsync_UnsupportedRequiredEnvironmentCheck_FailsClosed(EnvironmentCheckType type)
    {
        var yaml = EnvironmentStageYaml();
        var step = new PipelineStepRun { Id = 2, StageName = "ship", StepName = "publish", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "ship", step);
        ArrangeEnvironment(new EnvironmentCheck { Name = "unsupported", Type = type, IsRequired = true });

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    public async Task AdvanceStageAsync_MissingRestEnvironmentCheckConfiguration_FailsClosed(string? configuration)
    {
        var yaml = EnvironmentStageYaml();
        var step = new PipelineStepRun { Id = 2, StageName = "ship", StepName = "publish", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "ship", step);
        ArrangeEnvironment(new EnvironmentCheck
        {
            Name = "callback",
            Type = EnvironmentCheckType.RestCallback,
            IsRequired = true,
            Configuration = configuration
        });

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/health")]
    [InlineData("http://localhost/health")]
    [InlineData("http://10.1.2.3/health")]
    [InlineData("http://172.16.0.1/health")]
    [InlineData("http://192.168.1.1/health")]
    [InlineData("http://169.254.169.254/health")]
    [InlineData("http://100.64.0.1/health")]
    public async Task AdvanceStageAsync_UnsafeRestEnvironmentCheck_FailsClosedBeforeHttp(string url)
    {
        var yaml = EnvironmentStageYaml();
        var step = new PipelineStepRun { Id = 2, StageName = "ship", StepName = "publish", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "ship", step);
        ArrangeEnvironment(new EnvironmentCheck
        {
            Name = "callback",
            Type = EnvironmentCheckType.RestCallback,
            IsRequired = true,
            Configuration = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["url"] = url })
        });

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
        _httpClientFactoryMock.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [Fact]
    public async Task AdvanceStageAsync_PublicRestEnvironmentCheck_UsesNamedClientAndDispatchesOnSuccess()
    {
        var yaml = EnvironmentStageYaml();
        var step = new PipelineStepRun { Id = 2, StageName = "ship", StepName = "publish", PipelineRunId = 1 };
        ArrangeAdvanceToStage(yaml, "ship", step);
        ArrangeEnvironment(new EnvironmentCheck
        {
            Name = "callback",
            Type = EnvironmentCheckType.RestCallback,
            IsRequired = true,
            TimeoutSeconds = 5,
            Configuration = "{\"url\":\"https://1.1.1.1/health\"}"
        });
        _httpClientFactoryMock.CreateClient(PipelineEnvironmentCheckGuard.EnvironmentCheckHttpClient)
            .Returns(new HttpClient(new StaticResponseHandler(System.Net.HttpStatusCode.NoContent)));

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Assigned, step.Status);
        _repoMock.Received(1).TrackTask(Arg.Any<ServerTask>());
        _httpClientFactoryMock.Received(1).CreateClient(PipelineEnvironmentCheckGuard.EnvironmentCheckHttpClient);
    }

    [Fact]
    public async Task AdvanceStageAsync_OsMatrix_DispatchesLegsToOsAppropriateAgents()
    {
        // S-FEAT-21: an `os` matrix axis fans the same steps out across OS-appropriate runners.
        var yaml = """
            name: matrix
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: fan
                matrix:
                  os: [linux, windows]
                steps:
                  - name: pkg
                    shell: echo pkg
            """;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns(
            [
                new PipelineStepRun { Id = 2, StageName = "fan", StepName = "pkg [linux]", MatrixLeg = "linux", PipelineRunId = 1 },
                new PipelineStepRun { Id = 3, StageName = "fan", StepName = "pkg [windows]", MatrixLeg = "windows", PipelineRunId = 1 }
            ]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.GetActiveStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);
        // Stage resolves to a Linux runner (no os constraint); the windows leg resolves its own.
        _repoMock.FindAnyOnlineRunnerAsync(Arg.Any<int?>(), OsType.Unknown, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-runner", OsType = OsType.Linux });
        _repoMock.FindAnyOnlineRunnerAsync(Arg.Any<int?>(), OsType.Windows, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 20, Name = "windows-runner", OsType = OsType.Windows });
        _repoMock.TrackTask(Arg.Any<ServerTask>());
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        // Linux leg lands on the Linux runner; Windows leg lands on the Windows runner.
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t => t.Name == "pkg [linux]" && t.ServerId == 10));
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t => t.Name == "pkg [windows]" && t.ServerId == 20));
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_OsMatrix_NoRunnerForLeg_FailsRunInsteadOfStalling()
    {
        // S-FEAT-21 regression: a matrix os leg with no available runner must fail the run, not
        // strand it Running with no in-flight task (which would never re-trigger advancement).
        var yaml = """
            name: matrix
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: fan
                matrix:
                  os: [windows]
                steps:
                  - name: pkg
                    shell: echo pkg
            """;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Running,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun { Id = 2, StageName = "fan", StepName = "pkg [windows]", MatrixLeg = "windows", PipelineRunId = 1 }]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.GetActiveStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);
        // Stage resolves to a Linux runner; there is NO Windows runner for the windows leg.
        _repoMock.FindAnyOnlineRunnerAsync(Arg.Any<int?>(), OsType.Unknown, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-runner", OsType = OsType.Linux });
        _repoMock.FindAnyOnlineRunnerAsync(Arg.Any<int?>(), OsType.Windows, Arg.Any<CancellationToken>())
            .Returns((Server?)null);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.AppendRunWarningsAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        await _repoMock.Received().AppendRunWarningsAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(w => w.Any(m => m.Contains("no online runner"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_TriggerStep_MissingChildIsMaterializedFromImmutableGitRevision()
    {
        const string commit = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        var parentYaml = """
            name: release
            trigger: manual
            stages:
              - name: qa
                agent: linux-01
                steps:
                  - name: compatibility
                    type: trigger
                    pipeline: qa-with-rollback
            """;
        var childYaml = """
            name: qa-with-rollback
            trigger: manual
            stages:
              - name: validate
                agent: linux-01
                steps:
                  - name: test
                    shell: dotnet test
            """;
        var step = new PipelineStepRun { Id = 2, StageName = "qa", StepName = "compatibility", PipelineRunId = 1 };
        ArrangeAdvanceToStage(parentYaml, "qa", step);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => step.Status == TaskExecutionStatus.Pending ? [step] : []);
        var parent = new Pipeline
        {
            Id = 1,
            Name = "release",
            ProjectId = 7,
            CreatedByUsername = "alice",
            YamlDefinition = parentYaml
        };
        var parentRun = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            CommitHash = commit,
            BranchName = "main",
            AdditionalVariablesJson = "{}",
            ResolvedVariablesJson = "{}",
            Pipeline = parent
        };
        Pipeline? materialized = null;
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(parentRun);
        _repoMock.GetPipelineProjectIdAsync(parent, Arg.Any<CancellationToken>()).Returns(7);
        _repoMock.FindPipelineByNameAndProjectAsync("qa-with-rollback", 7, Arg.Any<CancellationToken>())
            .Returns(_ => materialized);
        _pipelineGitMock.ReadProjectPipelineYamlAtRevisionAsync(7, "qa-with-rollback", commit, Arg.Any<CancellationToken>())
            .Returns(childYaml);
        _repoMock.AddPipelineAsync(Arg.Do<Pipeline>(pipeline =>
            {
                pipeline.Id = 2;
                materialized = pipeline;
            }), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.FindPipelineAsync(2, Arg.Any<CancellationToken>()).Returns(_ => materialized);
        _repoMock.GetPipelineProjectIdAsync(Arg.Is<Pipeline>(pipeline => pipeline.Id == 2), Arg.Any<CancellationToken>())
            .Returns(7);
        _repoMock.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([42]);
        _authzMock.HasPermissionAsync("alice", ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);
        _repoMock.AppendRunWarningsAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(materialized);
        Assert.Equal("qa-with-rollback", materialized.Name);
        Assert.Equal(7, materialized.ProjectId);
        Assert.Equal("main", materialized.SourceBranch);
        Assert.Equal("alice", materialized.CreatedByUsername);
        Assert.Equal(childYaml, materialized.YamlDefinition);
        await _repoMock.Received(1).AddPipelineAsync(materialized, Arg.Any<CancellationToken>());
        await _pipelineGitMock.Received(2).ReadProjectPipelineYamlAtRevisionAsync(
            7, "qa-with-rollback", commit, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_TriggerStep_ChildOwnerLacksAdmin_RunsCleanupBeforeFinalizing()
    {
        // Prod regression (run #5): a `type: trigger` step whose child pipeline's OWNER fails the F-EXEC-1b
        // Server.Admin gate settles Failed synchronously with NO child run. The scheduler must persist that
        // failure and dispatch System:Cleanup rather than waiting for the 1h backstop or finalizing too early;
        // the warning must also NAME the offending owner so the cause is self-evident.
        var parentYaml = """
            name: aetheus-release
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: echo build
              - name: ci
                agent: linux-01
                depends_on:
                  - build
                steps:
                  - name: Build & test
                    type: trigger
                    pipeline: aetheus-ci
            """;
        var childYaml = """
            name: aetheus-ci
            trigger: manual
            stages:
              - name: compile
                os: linux
                steps:
                  - name: build
                    shell: dotnet build
            """;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>()).Returns([]);
        var parentRun = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { Id = 1, Name = "aetheus-release", YamlDefinition = parentYaml }
        };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(parentRun);
        var triggerStep = new PipelineStepRun { Id = 2, StageName = "ci", StepName = "Build & test", PipelineRunId = 1 };
        var cleanupStep = new PipelineStepRun
        {
            Id = 3,
            StageName = PipelineRunHelpers.SystemCleanupStage,
            StepName = "Cleanup",
            PipelineRunId = 1,
            IsSystem = true
        };
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([triggerStep], [cleanupStep]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.GetTerminalStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build", "ci"]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(false, true);
        _repoMock.GetActiveStageNamesAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineIdForRunAsync(1, Arg.Any<CancellationToken>()).Returns(1);
        _repoMock.GetPipelineOrganizationIdAsync(1, Arg.Any<CancellationToken>()).Returns((int?)null);
        // The ci stage still needs a runner resolved (the trigger step itself uses none).
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });

        // Trigger resolution: the child pipeline exists, is owned by the un-authorizable "system" sentinel,
        // and resolves to runner id 1 - on which "system" has no Server.Admin (authz mock defaults false).
        var childPipeline = new Pipeline { Id = 2, Name = "aetheus-ci", CreatedByUsername = "system", YamlDefinition = childYaml };
        _repoMock.GetPipelineProjectIdAsync(Arg.Any<Pipeline>(), Arg.Any<CancellationToken>()).Returns(7);
        _repoMock.FindPipelineByNameAndProjectAsync("aetheus-ci", 7, Arg.Any<CancellationToken>()).Returns(childPipeline);
        _repoMock.FindPipelineAsync(2, Arg.Any<CancellationToken>()).Returns(childPipeline);
        _repoMock.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([1]);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.AppendRunWarningsAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        // No child run is created. The synchronous failure is persisted, then cleanup becomes the only
        // in-flight task; its completion will re-enter the scheduler and finalize the original failure.
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "Cleanup"));
        Assert.Equal(TaskExecutionStatus.Assigned, cleanupStep.Status);
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        // The warning names the offending owner ('system') so the cause is self-evident.
        await _repoMock.Received().AppendRunWarningsAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(w => w.Any(m => m.Contains("'system'") && m.Contains("lacks Server.Admin"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_ContainerIsolation_DispatchesHardenedContainerTask()
    {
        var yaml = """
            name: deploy
            trigger: manual
            isolation:
              mode: container
              image: mcr.microsoft.com/dotnet/sdk:9.0@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
              network: none
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: deploy
                agent: linux-01
                steps:
                  - name: deploy-app
                    shell: deploy.sh
            """;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun { Id = 2, StageName = "deploy", StepName = "deploy-app", PipelineRunId = 1 }]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        // Container isolation requires a Docker-capable runner (fail-closed enforcement).
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01", DockerAvailable = true });
        _repoMock.TrackTask(Arg.Any<ServerTask>());
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t =>
            t.Name == "deploy-app"
            && t.Executor == ExecutorType.Container
            && t.ContainerImage == "mcr.microsoft.com/dotnet/sdk:9.0@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            && t.ContainerNetwork == "none"));
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(Arg.Any<int>(), PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_ContainerIsolatedScanner_UsesDockerVisibleRunWorkspace()
    {
        var yaml = """
            name: quality
            trigger: manual
            stages:
              - name: prepare
                agent: linux-01
                steps:
                  - name: install
                    shell: npm ci
              - name: scan
                agent: linux-01
                isolation:
                  mode: container
                  image: node:24.4.1-alpine@sha256:820e86612c21d0636580206d802a726f2595366e1b867e564cbc652024151e8a
                  network: none
                depends_on: [prepare]
                steps:
                  - name: eslint
                    type: scanner
                    scanner: eslint
            """;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "prepare", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "prepare", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Running,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { Id = 1, YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun
            {
                Id = 2,
                StageName = "scan",
                StepName = "eslint",
                PipelineRunId = 1
            }]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "prepare"]);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server
            {
                Id = 10,
                Name = "linux-01",
                PipelineRunnerEnabled = true,
                DockerAvailable = true
            });
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "prepare", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task =>
            task.Name == "eslint"
            && task.Operation == OperationKind.PipelineRunScanner
            && task.EnvironmentVariables.Contains(
                "\"AETHEUS_WORKSPACE_MODE\":\"container\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AdvanceStageAsync_ContainerIsolation_NoDocker_BlocksRun()
    {
        var yaml = """
            name: deploy
            trigger: manual
            isolation:
              mode: container
              image: mcr.microsoft.com/dotnet/sdk:9.0@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: deploy
                agent: linux-01
                steps:
                  - name: deploy-app
                    shell: deploy.sh
            """;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Running,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun { Id = 2, StageName = "deploy", StepName = "deploy-app", PipelineRunId = 1 }]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        // Runner has NO Docker - container isolation must block, not silently downgrade.
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01", DockerAvailable = false });
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>())
            .Returns(false);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.AppendRunWarningsAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(t => t.Name == "deploy-app"));
        await _repoMock.Received(1).AppendRunWarningsAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(w => w.Any(m => m.Contains("no Docker available"))),
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    // R-32: aetheus-demo reset the demo, then was refused at its vhost stage on a template token the
    // libraries no longer defined. The template a later stage renders is checked before the run exists.
    private const string DestructiveThenVhostYaml = """
        name: demo
        trigger: manual
        variables:
          APP_HOST: app.example.test
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: run
                shell: echo reset the demo
          - name: vhosts
            agent: linux-01
            depends_on: [build]
            steps:
              - name: Apply the versioned HTTP vhosts
                type: apache-config
                config_files:
                  "$(APP_HOST).conf": ".pipeline/configs/apache/app-http.conf"
        """;

    [Fact]
    public async Task R32_TriggerRunAsync_ConfigTemplateUsesAnUndefinedVariable_RefusedBeforeAnyStageRuns()
    {
        SetupTriggerRunMocks(DestructiveThenVhostYaml);
        _configRepositoryMock.ReadAsync(
                7, ".pipeline/configs/apache/app-http.conf", DefaultCommit, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("ServerName #{APP_HOST}#\nServerAlias #{API_HOST_ALIAS}#\n");

        var error = await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken));

        Assert.Contains("'API_HOST_ALIAS'", error.Message, StringComparison.Ordinal);
        Assert.Contains(".pipeline/configs/apache/app-http.conf", error.Message, StringComparison.Ordinal);
        Assert.Contains("Apply the versioned HTTP vhosts", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'APP_HOST'", error.Message, StringComparison.Ordinal);
        await _repoMock.DidNotReceive().AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task R32_TriggerRunAsync_ConfigTemplateTokensAllDefined_Launches()
    {
        SetupTriggerRunMocks(DestructiveThenVhostYaml);
        _configRepositoryMock.ReadAsync(
                7, ".pipeline/configs/apache/app-http.conf", DefaultCommit, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("ServerName #{APP_HOST}#\nDocumentRoot #{WORKSPACE}#\n");

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
    }

    /// <summary>R-20: the optional parameter that names the target has no value and no default. At the REAL
    /// launch (manual, scheduled, webhook, trigger step: they all end here) the stage would be left with
    /// no selector and dispatch would hand it the first online runner, a deployment on a server nobody
    /// chose. The launch is refused, before any run row exists, and says which parameter is empty.</summary>
    [Fact]
    public async Task R20_TriggerRunAsync_TargetParameterIsEmpty_RefusesTheLaunchInsteadOfUsingAnyRunner()
    {
        SetupTriggerRunMocks(DeployServerParameterYaml);

        var error = await Assert.ThrowsAsync<BadRequestException>(() => _sut.TriggerRunAsync(
            1, parameters: new Dictionary<string, string> { ["candidateVersion"] = "1.0.0" },
            ct: TestContext.Current.CancellationToken));

        Assert.Contains("'deployServer'", error.Message, StringComparison.Ordinal);
        Assert.Contains("Prepare host", error.Message, StringComparison.Ordinal);
        Assert.Contains("Deploy", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("${{", error.Message, StringComparison.Ordinal);
        await _repoMock.DidNotReceive().AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
        await _repoMock.DidNotReceive().FindAnyOnlineRunnerAsync(
            Arg.Any<int?>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R20_TriggerRunAsync_TargetParameterIsFilled_Launches()
    {
        SetupTriggerRunMocks(DeployServerParameterYaml);
        _repoMock.FindOnlineServerByAgentAsync("vps-prod", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 9, Name = "vps-prod" });

        await _sut.TriggerRunAsync(
            1, parameters: new Dictionary<string, string> { ["candidateVersion"] = "1.0.0", ["deployServer"] = "vps-prod" },
            ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A stage that never named a target keeps taking any online runner: only a target BUILT FROM
    /// a parameter that is empty is refused.</summary>
    [Fact]
    public async Task R20_TriggerRunAsync_StageWithoutAnyTarget_StillLaunches()
    {
        const string yaml = """
            name: build
            trigger: manual
            parameters:
              - name: note
                type: string
                required: false
            stages:
              - name: build
                steps:
                  - name: compile
                    shell: dotnet build
            """;
        SetupTriggerRunMocks(yaml);

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
    }

    // --- ResolveVariablesAsync tests (via TriggerRunAsync) ---

    [Fact]
    public async Task TriggerRunAsync_DuplicateDeploymentTarget_BlocksBeforeRunCreation()
    {
        const string yaml = """
            name: duplicate-deploy
            trigger: manual
            stages:
              - name: first
                agent: prod-01
                steps:
                  - name: deploy-first
                    type: deploy
                    app: portfolio
              - name: second
                agent: prod-01
                steps:
                  - name: deploy-second
                    type: deploy
                    app: portfolio
            """;
        SetupTriggerRunMocks(yaml);

        var error = await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken));

        Assert.Contains("Duplicate deployment target 'portfolio'", error.Message, StringComparison.Ordinal);
        Assert.Contains("deploy-first", error.Message, StringComparison.Ordinal);
        Assert.Contains("deploy-second", error.Message, StringComparison.Ordinal);
        await _repoMock.DidNotReceive().AddPipelineRunAsync(
            Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_ApacheConfigStep_RendersPinnedTemplateAndDispatchesConfigSet()
    {
        var yaml = """
            name: expose
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
              - name: expose
                agent: linux-01
                steps:
                  - name: config
                    type: apache-config
                    config_files:
                      "$(HOST).conf": ".pipeline/configs/apache/site.conf"
            """;
        ArrangeAdvanceToStageWithVariables(
            yaml, "expose", "{\"HOST\":\"app.example.test\"}",
            new PipelineStepRun { Id = 2, StageName = "expose", StepName = "config", PipelineRunId = 1 });
        _pipelineGitMock.ReadProjectConfigAtRevisionAsync(
                7, ".pipeline/configs/apache/site.conf", DefaultCommit,
                Arg.Any<CancellationToken>(), 11)
            .Returns("ServerName #{HOST}#\n");
        ServerTask? dispatched = null;
        _repoMock.When(repository => repository.TrackTask(Arg.Any<ServerTask>()))
            .Do(call => dispatched = call.Arg<ServerTask>());

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(dispatched);
        Assert.Equal(OperationKind.ApacheApplyConfigSet, dispatched.Operation);
        Assert.Equal("config-set", dispatched.Command);
        var environment = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            dispatched.EnvironmentVariables)!;
        var manifestJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
            environment["AETHEUS_APACHE_CONFIG_SET_B64"]));
        var manifest = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(manifestJson)!;
        var rendered = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
            manifest["app.example.test.conf"]));
        Assert.Equal("ServerName app.example.test\n", rendered);
        await _pipelineGitMock.Received(1).ReadProjectConfigAtRevisionAsync(
            7, ".pipeline/configs/apache/site.conf", DefaultCommit,
            Arg.Any<CancellationToken>(), 11);
    }

    [Fact]
    public async Task TriggerRunAsync_InlineVarsOverriddenByLibrary_LibraryWins()
    {
        var yaml = """
            name: deploy
            trigger: manual
            variables:
              APP_PORT: "3000"
              DB_HOST: inline-db
            variable_libraries:
              - my-lib
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: run
                    shell: echo $(APP_PORT) $(DB_HOST)
            """;

        SetupTriggerRunMocks(yaml);

        _varLibMock.ResolveLibrariesWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string> { ["APP_PORT"] = "8080" }, new HashSet<string> { "my-lib" }));

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        _repoMock.Received().TrackTask(Arg.Is<ServerTask>(t => t.Command.Contains("8080") && t.Command.Contains("inline-db")));
    }

    [Fact]
    public async Task TriggerRunAsync_VaultOverridesLibraryAndInline()
    {
        var yaml = """
            name: deploy
            trigger: manual
            variables:
              SECRET_KEY: inline-secret
            variable_libraries:
              - my-lib
            vaults:
              - my-vault
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: run
                    shell: echo $(SECRET_KEY)
            """;

        SetupTriggerRunMocks(yaml);

        _varLibMock.ResolveLibrariesWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string> { ["SECRET_KEY"] = "lib-secret" }, new HashSet<string> { "my-lib" }));
        _vaultMock.ResolveVaultSecretsWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string> { ["SECRET_KEY"] = "vault-secret" }, new HashSet<string> { "my-vault" }));

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        _repoMock.Received().TrackTask(Arg.Is<ServerTask>(t =>
            !t.Command.Contains("vault-secret", StringComparison.Ordinal)
            && t.Command.Contains("__AETHEUS_SECRET_", StringComparison.Ordinal)
            && t.EnvironmentVariables.Contains("vault-secret", StringComparison.Ordinal)));
    }

    // Project release-number allocation.

    private const string ReleaseNumberYaml = """
        name: deploy
        trigger: manual
        {0}
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: run
                shell: echo $(BUILD_RELEASENUMBER)
        """;

    [Fact]
    public async Task TriggerRunAsync_AReleasingPipeline_TakesTheProjectsNextReleaseNumber()
    {
        SetupTriggerRunMocks(string.Format(System.Globalization.CultureInfo.InvariantCulture, ReleaseNumberYaml, "release_number: allocate"));
        _repoMock.AllocateNextReleaseNumberAsync(7, "", Arg.Any<CancellationToken>()).Returns(4);

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AllocateNextReleaseNumberAsync(7, "", Arg.Any<CancellationToken>());
        await _repoMock.Received(1).AddPipelineRunAsync(
            Arg.Is<PipelineRun>(run => run.ReleaseNumber == 4
                && run.ResolvedVariablesJson!.Contains("\"BUILD_RELEASENUMBER\":\"4\"", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerRunAsync_AReleasingPipeline_CountsInTheSeriesOfTheCommitItReleases()
    {
        SetupTriggerRunMocks(string.Format(System.Globalization.CultureInfo.InvariantCulture, ReleaseNumberYaml, "release_number: allocate"));
        _pipelineGitMock.ReadVersionPropsAtRevisionAsync(7, DefaultCommit, Arg.Any<CancellationToken>(), 11)
            .Returns("<Project><PropertyGroup><VersionPrefix>1.4.0</VersionPrefix></PropertyGroup></Project>");
        _repoMock.AllocateNextReleaseNumberAsync(7, "1.4", Arg.Any<CancellationToken>()).Returns(0);

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AllocateNextReleaseNumberAsync(7, "1.4", Arg.Any<CancellationToken>());
        await _repoMock.Received(1).AddPipelineRunAsync(
            Arg.Is<PipelineRun>(run => run.ReleaseNumber == 0 && run.ReleaseSeries == "1.4"
                && run.ResolvedVariablesJson!.Contains("\"BUILD_RELEASESERIES\":\"1.4\"", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerRunAsync_AChildRun_BuildsWithTheReleaseNumberOfTheRunThatStartedIt()
    {
        SetupTriggerRunMocks(string.Format(System.Globalization.CultureInfo.InvariantCulture, ReleaseNumberYaml, string.Empty));
        _repoMock.GetRunReleaseAsync(55, Arg.Any<CancellationToken>()).Returns(new ReleaseNumberAssignment("1.3", 3));
        var upstream = new Dictionary<string, string> { ["UPSTREAM_RUN_ID"] = "55" };

        await _sut.TriggerRunAsync(1, upstream, ct: TestContext.Current.CancellationToken);

        await _repoMock.DidNotReceive().AllocateNextReleaseNumberAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repoMock.Received(1).AddPipelineRunAsync(
            Arg.Is<PipelineRun>(run => run.ReleaseNumber == 3 && run.ReleaseSeries == "1.3"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerRunAsync_ARunThatMakesNoRelease_ReadsTheLatestReleaseNumber()
    {
        SetupTriggerRunMocks(string.Format(System.Globalization.CultureInfo.InvariantCulture, ReleaseNumberYaml, string.Empty));
        _repoMock.GetLatestReleaseNumberAsync(7, "", Arg.Any<CancellationToken>()).Returns(2);

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        await _repoMock.DidNotReceive().AllocateNextReleaseNumberAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repoMock.Received(1).AddPipelineRunAsync(
            Arg.Is<PipelineRun>(run => run.ReleaseNumber == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerRunAsync_AnUnknownReleaseNumberMode_IsRefused()
    {
        SetupTriggerRunMocks(string.Format(System.Globalization.CultureInfo.InvariantCulture, ReleaseNumberYaml, "release_number: next"));

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken));

        await _repoMock.DidNotReceive().AllocateNextReleaseNumberAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerRunAsync_AdditionalVarsOverrideAll()
    {
        var yaml = """
            name: deploy
            trigger: manual
            variables:
              ENV: dev
            vaults:
              - my-vault
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: run
                    shell: echo $(ENV)
            """;

        SetupTriggerRunMocks(yaml);

        _vaultMock.ResolveVaultSecretsWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string> { ["ENV"] = "vault-env" }, new HashSet<string> { "my-vault" }));

        var additionalVars = new Dictionary<string, string> { ["ENV"] = "production" };
        await _sut.TriggerRunAsync(1, additionalVars, ct: TestContext.Current.CancellationToken);

        _repoMock.Received().TrackTask(Arg.Is<ServerTask>(t =>
            !t.Command.Contains("production", StringComparison.Ordinal)
            && t.Command.Contains("__AETHEUS_SECRET_", StringComparison.Ordinal)
            && t.EnvironmentVariables.Contains("production", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TriggerRunAsync_MissingVariable_RefusesTheLaunchAndNamesIt()
    {
        // Unresolved required variable names must be refused rather than sent literally to agents.
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: run
                    shell: echo $(UNDEFINED_VAR) done
            """;

        SetupTriggerRunMocks(yaml);

        var error = await Assert.ThrowsAsync<BadRequestException>(
            () => _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken));

        Assert.Contains("UNDEFINED_VAR", error.Message, StringComparison.Ordinal);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task TriggerRunAsync_ShellCommandSubstitution_IsNotTreatedAsAVariable()
    {
        // The counterpart: $(date) is a POSIX command substitution the agent's shell executes, not a
        // broken reference. The guard matches UPPER_SNAKE names only, so it must stay untouched.
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: run
                    shell: echo "built at $(date) by $(whoami)"
            """;

        SetupTriggerRunMocks(yaml);

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(
            Arg.Is<ServerTask>(t => t.Command.Contains("$(date)", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TriggerRunAsync_MultipleVarsInSameCommand_AllSubstituted()
    {
        var yaml = """
            name: deploy
            trigger: manual
            variables:
              HOST: localhost
              PORT: "5000"
              PATH_PREFIX: /api
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: run
                    shell: curl $(HOST):$(PORT)$(PATH_PREFIX)/health
            """;

        SetupTriggerRunMocks(yaml);

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        _repoMock.Received().TrackTask(Arg.Is<ServerTask>(t => t.Command.Contains("curl localhost:5000/api/health")));
    }

    [Fact]
    public async Task TriggerRunAsync_EmptyVarDict_NoSubstitution()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: run
                    shell: echo hello
            """;

        SetupTriggerRunMocks(yaml);

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        _repoMock.Received().TrackTask(Arg.Is<ServerTask>(t => t.Command.Contains("echo hello")));
    }

    [Fact]
    public async Task TriggerRunAsync_CaseInsensitiveKeyMatching()
    {
        var yaml = """
            name: deploy
            trigger: manual
            variables:
              my_var: value1
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: run
                    shell: echo $(MY_VAR)
            """;

        SetupTriggerRunMocks(yaml);

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        _repoMock.Received().TrackTask(Arg.Is<ServerTask>(t => t.Command.Contains("echo value1")));
    }

    // --- DryRunAsync ---

    [Fact]
    public async Task DryRunAsync_PipelineNotFound_ReturnsNull()
    {
        _repoMock.FindPipelineAsync(99, Arg.Any<CancellationToken>())
            .Returns((Pipeline?)null);

        var result = await _sut.DryRunAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task DryRunAsync_InvalidYaml_ReturnsNull()
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Bad", YamlDefinition = "{{invalid", Runs = [] });

        var result = await _sut.DryRunAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task DryRunAsync_MissingLibrary_ReturnsWarning()
    {
        var yaml = """
            name: deploy
            trigger: manual
            variable_libraries:
              - non-existent-lib
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: run
                    shell: echo hi
            """;

        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = yaml, Runs = [] });
        _varLibMock.ResolveLibrariesWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string>(), new HashSet<string>()));

        var result = await _sut.DryRunAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result.Warnings, w => w.Contains("non-existent-lib") && w.Contains("not found"));
    }

    [Fact]
    public async Task DryRunAsync_MissingVault_BlocksBeforeExecution()
    {
        var yaml = """
            name: deploy
            trigger: manual
            vaults:
              - missing-vault
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: run
                    shell: echo hi
            """;

        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = yaml, Runs = [] });
        _vaultMock.ResolveVaultSecretsWithCrossAccessAndNamesAsync(Arg.Any<List<string>>(), 7, Arg.Any<CancellationToken>())
            .Returns((new Dictionary<string, string>(), new HashSet<string>()));

        var exception = await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(
            () => _sut.DryRunAsync(1, ct: TestContext.Current.CancellationToken));

        Assert.Contains("missing-vault", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DryRunAsync_ValidPipeline_ReturnsResolvedStages()
    {
        var yaml = """
            name: deploy
            trigger: manual
            variables:
              HOST: localhost
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: echo $(HOST)
            """;

        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = yaml, Runs = [] });

        var result = await _sut.DryRunAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Single(result.Stages);
        Assert.Equal("build", result.Stages[0].StageName);
        Assert.Equal("echo localhost", result.Stages[0].Steps[0].ResolvedCommand);
        Assert.Empty(result.Warnings);
    }

    // --- PreflightAsync ---

    [Fact]
    public async Task PreflightAsync_PipelineNotFound_ReturnsNull()
    {
        _repoMock.FindPipelineAsync(99, Arg.Any<CancellationToken>())
            .Returns((Pipeline?)null);

        Assert.Null(await _sut.PreflightAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreflightAsync_FlagsStagesWithNoMatchingServer()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: make
              - name: ship
                agent: ghost-agent
                steps:
                  - name: deploy
                    shell: deploy.sh
            """;
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = yaml, Runs = [] });
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 7, Name = "srv-linux" });
        _repoMock.FindOnlineServerByAgentAsync("ghost-agent", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns((Server?)null);

        var result = await _sut.PreflightAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(2, result.Stages.Count);

        var build = result.Stages.Single(s => s.StageName == "build");
        Assert.True(build.Resolved);
        Assert.Equal("srv-linux", build.ServerName);
        Assert.Equal(PreflightTargetKind.Agent, build.TargetKind);
        Assert.Equal("linux-01", build.Target);
        Assert.Null(build.Reason);

        var ship = result.Stages.Single(s => s.StageName == "ship");
        Assert.False(ship.Resolved);
        Assert.Null(ship.ServerName);
        Assert.NotNull(ship.Reason);
        Assert.Contains("ghost-agent", ship.Reason);
    }

    [Fact]
    public async Task PreflightAsync_DeployStep_UsesDeploymentTargetResolver()
    {
        var yaml = """
            name: deploy
            stages:
              - name: ship
                agent: deploy-01
                steps:
                  - name: publish
                    type: deploy
                    app: api
            """;
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = yaml, Runs = [] });
        _repoMock.FindOnlineDeployTargetAsync(
                null, null, "deploy-01", OsType.Unknown, Arg.Is<int?>(organization => organization == null), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 8, Name = "deployment-host" });

        var result = await _sut.PreflightAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal("deployment-host", Assert.Single(result!.Stages).ServerName);
        await _repoMock.Received(1).FindOnlineDeployTargetAsync(
            null, null, "deploy-01", OsType.Unknown, Arg.Is<int?>(organization => organization == null), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().FindOnlineServerByAgentAsync(
            Arg.Any<string>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>());
    }

    private const string DeployServerParameterYaml = """
        name: deploy
        trigger: manual
        parameters:
          - name: candidateVersion
            display_name: Candidate release version
            display_name_fr: Version de la release candidate
            type: string
            required: true
          - name: deployServer
            display_name: Production server
            display_name_fr: Serveur de production
            type: string
            required: false
        stages:
          - name: Prepare host
            agent: "${{ parameters.deployServer }}"
            steps:
              - name: prepare
                shell: prepare.sh
          - name: Deploy
            agent: "${{ parameters.deployServer }}"
            steps:
              - name: publish
                shell: deploy.sh
        """;

    /// <summary>R-20: the optional parameter that names the target has no value and no default. The launch
    /// turns it into an empty string, so the preview must say that parameter is empty, not look for an
    /// agent literally called "${{ parameters.deployServer }}" and report it offline.</summary>
    [Fact]
    public async Task PreflightAsync_EmptyTargetParameter_IsReportedAsEmpty_AndNoAgentIsSearched()
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = DeployServerParameterYaml, Runs = [] });

        var result = await _sut.PreflightAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(2, result.Stages.Count);
        Assert.All(result.Stages, stage =>
        {
            Assert.False(stage.Resolved);
            var empty = Assert.IsType<PreflightEmptyParameterDto>(stage.EmptyTargetParameter);
            Assert.Equal("deployServer", empty.Name);
            Assert.Equal("Production server", empty.Label);
            Assert.Equal("Serveur de production", empty.LabelFr);
            Assert.DoesNotContain("${{", stage.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("${{", stage.Target, StringComparison.Ordinal);
        });
        await _repoMock.DidNotReceive().FindOnlineServerByAgentAsync(
            Arg.Any<string>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().FindAnyOnlineRunnerAsync(
            Arg.Any<int?>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>());
    }

    /// <summary>R-26: the preview runs before the user has answered, so a required parameter that has no
    /// value yet is not a warning (the launch enforces it, and the dialog says so under the field).</summary>
    [Fact]
    public async Task PreflightAsync_MissingRequiredParameter_RaisesNoWarning()
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = DeployServerParameterYaml, Runs = [] });

        var result = await _sut.PreflightAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result!.Warnings, warning => warning.Contains("candidateVersion", StringComparison.Ordinal));
    }

    /// <summary>The value the user chose in the launch dialog names the target, exactly as the launch
    /// reads it.</summary>
    [Fact]
    public async Task PreflightAsync_SuppliedTargetParameter_ResolvesTheNamedServer()
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = DeployServerParameterYaml, Runs = [] });
        _repoMock.FindOnlineServerByAgentAsync("vps-prod", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 9, Name = "vps-prod" });

        var result = await _sut.PreflightAsync(
            1, new Dictionary<string, string> { ["deployServer"] = "vps-prod" }, TestContext.Current.CancellationToken);

        Assert.All(result!.Stages, stage =>
        {
            Assert.True(stage.Resolved);
            Assert.Equal("vps-prod", stage.ServerName);
            Assert.Null(stage.EmptyTargetParameter);
        });
    }

    // --- Shared setup helper ---

    private void SetupTriggerRunMocks(string yaml)
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = yaml, Runs = [] });
        _repoMock.AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.TrackPipelineStepRun(Arg.Any<PipelineStepRun>());
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun { Id = 1, StageName = "build", StepName = "run", PipelineRunId = 1 }]);
        _repoMock.GetCompletedStageNamesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage]);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });
        _repoMock.FindAnyOnlineRunnerAsync(Arg.Any<int?>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });
        _repoMock.GetPipelineRunWithPipelineAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { Name = "Deploy", YamlDefinition = yaml }
            });
        _repoMock.TrackTask(Arg.Any<ServerTask>());
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Running,
                Pipeline = new Pipeline { Name = "Deploy" },
                StepRuns = []
            });
    }

    // --- P-24: Matrix Expansion ---

    [Fact]
    public async Task TriggerRunAsync_MatrixStage_CreatesMultipleStepRuns()
    {
        var yaml = """
            name: matrix-test
            trigger: manual
            stages:
              - name: test
                agent: linux-01
                matrix:
                  os:
                    - ubuntu
                    - windows
                  arch:
                    - x64
                    - arm64
                steps:
                  - name: run-test
                    shell: echo testing
            """;

        var stepRunsAdded = new List<PipelineStepRun>();
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Matrix", YamlDefinition = yaml, Runs = [] });
        _repoMock.AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.When(x => x.TrackPipelineStepRun(Arg.Any<PipelineStepRun>())).Do(ci => stepRunsAdded.Add(ci.Arg<PipelineStepRun>()));
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Running,
                Pipeline = new Pipeline { Name = "Matrix" },
                StepRuns = []
            });

        await _sut.TriggerRunAsync(1, ct: TestContext.Current.CancellationToken);

        // 2 os × 2 arch = 4 user step runs + 2 system (Prepare + Cleanup) = 6
        Assert.Equal(6, stepRunsAdded.Count);
        Assert.Contains(stepRunsAdded, s => s.MatrixLeg == "ubuntu-x64");
        Assert.Contains(stepRunsAdded, s => s.MatrixLeg == "ubuntu-arm64");
        Assert.Contains(stepRunsAdded, s => s.MatrixLeg == "windows-x64");
        Assert.Contains(stepRunsAdded, s => s.MatrixLeg == "windows-arm64");
    }

    // --- P-27: Retry Decrement ---

    [Fact]
    public async Task AdvanceStageAsync_FailedStepWithRetry_DecrementsAndResets()
    {
        var yaml = """
            name: retry-test
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: flaky
                    shell: make
                    retry_count: 2
            """;

        var failedStep = new PipelineStepRun
        {
            Id = 1,
            StageName = "build",
            StepName = "flaky",
            Status = TaskExecutionStatus.Failed,
            RetryCount = 2,
            ContinueOnError = false,
            ExitCode = 1,
            StartedAt = DateTime.UtcNow.AddSeconds(-5),
            CompletedAt = DateTime.UtcNow
        };

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([failedStep]);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, failedStep.RetryCount); // Decremented from 2 to 1
        Assert.Equal(TaskExecutionStatus.Pending, failedStep.Status);
        Assert.Null(failedStep.ExitCode);
        Assert.Null(failedStep.StartedAt);
        Assert.Null(failedStep.CompletedAt);
        // Should NOT advance to next stage or mark as failed
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(Arg.Any<int>(), Arg.Any<PipelineStatus>(), Arg.Any<CancellationToken>());
    }

    // --- P-27: Continue-on-Error ---

    [Fact]
    public async Task AdvanceStageAsync_AllFailuresContinuable_AdvancesToNextStage()
    {
        var yaml = """
            name: continue-test
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: optional
                    shell: echo maybe
                    continue_on_error: true
              - name: deploy
                agent: linux-01
                steps:
                  - name: deploy-app
                    shell: deploy.sh
            """;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun
            {
                Id = 1, StageName = "build", StepName = "optional",
                Status = TaskExecutionStatus.Failed, RetryCount = 0, ContinueOnError = true
            }]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun { Id = 2, StageName = "deploy", StepName = "deploy-app", PipelineRunId = 1 }]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });
        _repoMock.TrackTask(Arg.Any<ServerTask>());
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(t => t.Name == "deploy-app"));
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(Arg.Any<int>(), PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileRunAsync_DastPolicyRefusalOnContinuableScanner_AdvancesToNextStage()
    {
        var yaml = """
            name: dast-advisory
            trigger: manual
            stages:
              - name: dast
                environment: qa
                steps:
                  - name: passive
                    type: scanner
                    scanner: zap-passive
                    target_url: http://127.0.0.1:22056
                    continue_on_error: true
              - name: after
                agent: linux-01
                depends_on: [dast]
                steps:
                  - name: continue
                    shell: echo continued
            """;
        var scannerStep = new PipelineStepRun
        {
            Id = 10,
            PipelineRunId = 1,
            StageName = "dast",
            StepName = "passive",
            ContinueOnError = true
        };
        var nextStep = new PipelineStepRun
        {
            Id = 11,
            PipelineRunId = 1,
            StageName = "after",
            StepName = "continue"
        };
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { Id = 1, Name = "dast-advisory", YamlDefinition = yaml }
        };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPipelineIdForRunAsync(1, Arg.Any<CancellationToken>()).Returns(1);
        _repoMock.GetPipelineOrganizationIdAsync(1, Arg.Any<CancellationToken>()).Returns(4);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([scannerStep], [nextStep]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage], [PipelineRunHelpers.SystemPrepareStage, "dast"]);
        _repoMock.FindOnlineServerInEnvironmentInOrganizationAsync(
                "qa", OsType.Unknown, 4, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01", OrganizationId = 4 });
        _repoMock.FindOnlineServerByAgentInOrganizationAsync(
                "linux-01", OsType.Unknown, 4, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01", OrganizationId = 4 });
        _repoMock.FindEnvironmentByNameForProjectAsync("qa", 7, Arg.Any<CancellationToken>())
            .Returns(new Aetheus.Back.Data.Entities.Environment
            {
                Id = 2,
                Name = "qa",
                ProjectId = 7,
                Type = EnvironmentType.Testing,
                DastEnabled = false
            });

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, scannerStep.Status);
        Assert.Equal(-1, scannerStep.ExitCode);
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "continue"));
        await _repoMock.Received(1).AppendRunWarningsAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(warnings => warnings.Single().Contains("DAST is disabled", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_MixedFailures_NotAllContinuable_FailsRun()
    {
        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([
                new PipelineStepRun { Id = 1, StageName = "build", StepName = "optional", Status = TaskExecutionStatus.Failed, RetryCount = 0, ContinueOnError = true },
                new PipelineStepRun { Id = 2, StageName = "build", StepName = "critical", Status = TaskExecutionStatus.Failed, RetryCount = 0, ContinueOnError = false }
            ]);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    // --- CancelRunAsync ---

    private static readonly PipelineRunCancellation UserCancellation = PipelineRunCancellation.ByUser("alice");

    [Fact]
    public async Task R2103_CancelRunAsync_AuditsEachCancelledRun_WithItsAuthorAndReason()
    {
        // Cancellation records its author in the audit trail.
        var runs = new Dictionary<int, PipelineRun>
        {
            [1] = new() { Id = 1, PipelineId = 11, Pipeline = new Pipeline { Id = 11, Name = "release-fast" }, Status = PipelineStatus.Running },
            [2] = new() { Id = 2, PipelineId = 12, Pipeline = new Pipeline { Id = 12, Name = "child" }, Status = PipelineStatus.Pending }
        };
        _repoMock.GetPipelineRunWithPipelineAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => runs.GetValueOrDefault(call.ArgAt<int>(0)));
        _repoMock.GetTriggeredChildRunIdsAsync(1, Arg.Any<CancellationToken>()).Returns([2]);
        _repoMock.GetTriggeredChildRunIdsAsync(2, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.TryTransitionPipelineRunStatusAsync(
                Arg.Any<int>(), Arg.Any<PipelineStatus>(), PipelineStatus.Running, Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.HasAnyRunningStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        await _sut.CancelRunAsync(1, UserCancellation, TestContext.Current.CancellationToken);

        await _auditMock.Received(1).LogAsync("Cancelled", "PipelineRun", 1,
            "release-fast | by alice | Cancelled by a user", Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("Cancelled", "PipelineRun", 2,
            Arg.Is<string>(details => details.StartsWith("child | by alice | Parent run #1 was cancelled", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).RequestPipelineRunCancellationAsync(
            2, Arg.Is<PipelineRunCancellation>(cancellation => cancellation.By == "alice"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R2103_CancelRunAsync_ByTheSystem_IsAuditedAsSystem()
    {
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 1, Pipeline = new Pipeline { Name = "deploy" }, Status = PipelineStatus.Running });
        _repoMock.HasAnyRunningStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        await _sut.CancelRunAsync(1, PipelineRunCancellation.BySystem("Superseded by a newer push (latest wins)"),
            TestContext.Current.CancellationToken);

        await _auditMock.Received(1).LogAsync("Cancelled", "PipelineRun", 1,
            "deploy | by system | Superseded by a newer push (latest wins)", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R2103_ARunThatIsAlreadyOver_IsNeitherCancelledNorAudited()
    {
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 1, Status = PipelineStatus.Success });

        await _sut.CancelRunAsync(1, UserCancellation, TestContext.Current.CancellationToken);

        await _auditMock.DidNotReceive().LogAsync("Cancelled", Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelRunAsync_RunNotFound_ReturnsFalse()
    {
        _repoMock.GetPipelineRunWithPipelineAsync(99, Arg.Any<CancellationToken>())
            .Returns((PipelineRun?)null);

        var result = await _sut.CancelRunAsync(99, UserCancellation, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task CancelRunAsync_AlreadyCompleted_ReturnsFalse()
    {
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 1, Status = PipelineStatus.Success });

        var result = await _sut.CancelRunAsync(1, UserCancellation, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task CancelRunAsync_RunningPipeline_CancelsAndReturnsTrue()
    {
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 1, Status = PipelineStatus.Running });
        _repoMock.HasAnyRunningStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.CancelRunAsync(1, UserCancellation, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repoMock.Received(1).RequestPipelineRunCancellationAsync(
            1, UserCancellation, Arg.Any<CancellationToken>());
        await _repoMock.Received(1).CancelPendingStepRunsExceptStagesAsync(
            1, Arg.Is<IReadOnlyCollection<string>>(stages =>
                stages.Contains(PipelineRunHelpers.SystemCleanupStage)),
            Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().AppendRunWarningsAsync(
            1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().TryTransitionPipelineRunStatusAsync(
            1, PipelineStatus.Running, Arg.Any<PipelineStatus>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelRunAsync_RepeatedRequest_ReleasesOrphanRunningSteps()
    {
        var run = new PipelineRun
        {
            Id = 1,
            Status = PipelineStatus.Running,
            AdditionalVariablesJson =
                $"{{\"{PipelineRunHelpers.CancellationRequestedVariable}\":\"true\"}}"
        };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.HasAnyRunningStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.CancelRunAsync(1, UserCancellation, TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repoMock.Received(1).CancelOrphanedRunningStepRunsExceptStagesAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(stages =>
                stages.Contains(PipelineRunHelpers.SystemCleanupStage)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelRunAsync_ParentPipeline_CancelsEveryTriggeredDescendant()
    {
        var runs = new Dictionary<int, PipelineRun>
        {
            [1] = new() { Id = 1, PipelineId = 11, Pipeline = new Pipeline { Id = 11 }, Status = PipelineStatus.Running },
            [2] = new() { Id = 2, PipelineId = 12, Pipeline = new Pipeline { Id = 12 }, Status = PipelineStatus.WaitingForApproval },
            [3] = new() { Id = 3, PipelineId = 13, Pipeline = new Pipeline { Id = 13 }, Status = PipelineStatus.Pending }
        };
        _repoMock.GetPipelineRunWithPipelineAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => runs.GetValueOrDefault(call.ArgAt<int>(0)));
        _repoMock.GetTriggeredChildRunIdsAsync(1, Arg.Any<CancellationToken>()).Returns([2]);
        _repoMock.GetTriggeredChildRunIdsAsync(2, Arg.Any<CancellationToken>()).Returns([3]);
        _repoMock.GetTriggeredChildRunIdsAsync(3, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.TryTransitionPipelineRunStatusAsync(
                Arg.Any<int>(), Arg.Any<PipelineStatus>(), PipelineStatus.Running, Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.HasAnyRunningStepInRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.CancelRunAsync(1, UserCancellation, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repoMock.DidNotReceive().TryTransitionPipelineRunStatusAsync(
            1, PipelineStatus.Running, Arg.Any<PipelineStatus>(), Arg.Any<CancellationToken>());
        await _repoMock.Received(1).TryTransitionPipelineRunStatusAsync(
            2, PipelineStatus.WaitingForApproval, PipelineStatus.Running, Arg.Any<CancellationToken>());
        await _repoMock.Received(1).TryTransitionPipelineRunStatusAsync(
            3, PipelineStatus.Pending, PipelineStatus.Running, Arg.Any<CancellationToken>());
        await _repoMock.Received(3).RequestPipelineRunCancellationAsync(
            Arg.Any<int>(), Arg.Any<PipelineRunCancellation>(), Arg.Any<CancellationToken>());
        await _repoMock.Received(3).CancelPendingStepRunsExceptStagesAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
        await _clientProxyMock.DidNotReceive().SendCoreAsync(
            "PipelineRunCancelled", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelRunAsync_PreservesAlwaysTeardownUntilItFinishes()
    {
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 11,
            Pipeline = new Pipeline { Id = 11 },
            Status = PipelineStatus.Running,
            YamlSnapshot = """
                name: cancellable
                stages:
                  - name: Work
                    steps:
                      - name: run
                        shell: sleep 30
                  - name: Teardown
                    condition: always()
                    depends_on: [Work]
                    steps:
                      - name: cleanup
                        shell: docker compose down -v
                """
        };
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.HasAnyRunningStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.CancelRunAsync(1, UserCancellation, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repoMock.Received(1).CancelPendingStepRunsExceptStagesAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(stages =>
                stages.Contains("Teardown") && stages.Contains(PipelineRunHelpers.SystemCleanupStage)),
            Arg.Any<CancellationToken>());
    }

    // --- ResumeAfterApprovalAsync ---

    [Fact]
    public async Task ResumeAfterApprovalAsync_NotWaiting_DoesNothing()
    {
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 1, Status = PipelineStatus.Running });

        await _sut.ResumeAfterApprovalAsync(1, ct: TestContext.Current.CancellationToken);

        await _repoMock.DidNotReceive().TryTransitionPipelineRunStatusAsync(
            Arg.Any<int>(), Arg.Any<PipelineStatus>(), Arg.Any<PipelineStatus>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeAfterApprovalAsync_RunNotFound_DoesNothing()
    {
        _repoMock.GetPipelineRunWithPipelineAsync(99, Arg.Any<CancellationToken>())
            .Returns((PipelineRun?)null);

        await _sut.ResumeAfterApprovalAsync(99, ct: TestContext.Current.CancellationToken);

        await _repoMock.DidNotReceive().TryTransitionPipelineRunStatusAsync(
            Arg.Any<int>(), Arg.Any<PipelineStatus>(), Arg.Any<PipelineStatus>(), Arg.Any<CancellationToken>());
    }

    // --- FindReadyStages ---

    [Fact]
    public void FindReadyStages_NoDependencies_ReturnsAllPendingStages()
    {
        var definition = new PipelineYamlDefinition
        {
            Stages = [
                new PipelineStageDefinition { Name = "build" },
                new PipelineStageDefinition { Name = "test" }
            ]
        };
        var pendingSteps = new List<PipelineStepRun>
        {
            new() { StageName = "build" },
            new() { StageName = "test" }
        };

        var result = InvokeFindReadyStages(pendingSteps, definition, [PipelineRunHelpers.SystemPrepareStage]);

        Assert.Equal(2, result.Count);
        Assert.Contains("build", result);
        Assert.Contains("test", result);
    }

    [Fact]
    public void FindReadyStages_WithUnmetDependency_ExcludesStage()
    {
        var definition = new PipelineYamlDefinition
        {
            Stages = [
                new PipelineStageDefinition { Name = "build" },
                new PipelineStageDefinition { Name = "deploy", DependsOn = ["build"] }
            ]
        };
        var pendingSteps = new List<PipelineStepRun>
        {
            new() { StageName = "build" },
            new() { StageName = "deploy" }
        };

        var result = InvokeFindReadyStages(pendingSteps, definition, [PipelineRunHelpers.SystemPrepareStage]);

        Assert.Single(result);
        Assert.Equal("build", result[0]);
    }

    [Fact]
    public void FindReadyStages_WithMetDependency_IncludesStage()
    {
        var definition = new PipelineYamlDefinition
        {
            Stages = [
                new PipelineStageDefinition { Name = "build" },
                new PipelineStageDefinition { Name = "deploy", DependsOn = ["build"] }
            ]
        };
        var pendingSteps = new List<PipelineStepRun> { new() { StageName = "deploy" } };

        var result = InvokeFindReadyStages(pendingSteps, definition, [PipelineRunHelpers.SystemPrepareStage, "build"]);

        Assert.Single(result);
        Assert.Equal("deploy", result[0]);
    }

    [Fact]
    public void FindReadyStages_UnknownStage_ExcludesIt()
    {
        var definition = new PipelineYamlDefinition
        {
            Stages = [new PipelineStageDefinition { Name = "build" }]
        };
        var pendingSteps = new List<PipelineStepRun> { new() { StageName = "unknown" } };

        var result = InvokeFindReadyStages(pendingSteps, definition, []);

        Assert.Empty(result);
    }

    [Fact]
    public void FindReadyStages_MultipleDepsMet_IncludesStage()
    {
        var definition = new PipelineYamlDefinition
        {
            Stages = [
                new PipelineStageDefinition { Name = "build" },
                new PipelineStageDefinition { Name = "test" },
                new PipelineStageDefinition { Name = "deploy", DependsOn = ["build", "test"] }
            ]
        };
        var pendingSteps = new List<PipelineStepRun> { new() { StageName = "deploy" } };

        var result = InvokeFindReadyStages(pendingSteps, definition, [PipelineRunHelpers.SystemPrepareStage, "build", "test"]);

        Assert.Single(result);
        Assert.Equal("deploy", result[0]);
    }

    [Fact]
    public void FindReadyStages_PartialDepsMet_ExcludesStage()
    {
        var definition = new PipelineYamlDefinition
        {
            Stages = [
                new PipelineStageDefinition { Name = "deploy", DependsOn = ["build", "test"] }
            ]
        };
        var pendingSteps = new List<PipelineStepRun> { new() { StageName = "deploy" } };

        var result = InvokeFindReadyStages(pendingSteps, definition, [PipelineRunHelpers.SystemPrepareStage, "build"]);

        Assert.Empty(result);
    }

    [Fact]
    public void FindReadyStages_AlwaysStage_AcceptsTerminalFailedDependency()
    {
        var definition = new PipelineYamlDefinition
        {
            Stages =
            [
                new PipelineStageDefinition { Name = "e2e" },
                new PipelineStageDefinition { Name = "teardown", Condition = "always()", DependsOn = ["e2e"] }
            ]
        };
        var pendingSteps = new List<PipelineStepRun> { new() { StageName = "teardown" } };

        var result = InvokeFindReadyStages(
            pendingSteps,
            definition,
            [PipelineRunHelpers.SystemPrepareStage],
            [PipelineRunHelpers.SystemPrepareStage, "e2e"]);

        Assert.Equal(["teardown"], result);
    }

    [Fact]
    public void FindReadyStages_NormalStage_RejectsTerminalFailedDependency()
    {
        var definition = new PipelineYamlDefinition
        {
            Stages =
            [
                new PipelineStageDefinition { Name = "e2e" },
                new PipelineStageDefinition { Name = "deploy", DependsOn = ["e2e"] }
            ]
        };
        var pendingSteps = new List<PipelineStepRun> { new() { StageName = "deploy" } };

        var result = InvokeFindReadyStages(
            pendingSteps,
            definition,
            [PipelineRunHelpers.SystemPrepareStage],
            [PipelineRunHelpers.SystemPrepareStage, "e2e"]);

        Assert.Empty(result);
    }

    [Fact]
    public void FindReadyStages_SystemCleanup_AcceptsTerminalFailedUserStages()
    {
        var definition = new PipelineYamlDefinition
        {
            Stages =
            [
                new PipelineStageDefinition { Name = "e2e" },
                new PipelineStageDefinition { Name = "teardown", Condition = "always()", DependsOn = ["e2e"] }
            ]
        };
        var pendingSteps = new List<PipelineStepRun>
        {
            new() { StageName = PipelineRunHelpers.SystemCleanupStage, IsSystem = true }
        };

        var result = InvokeFindReadyStages(
            pendingSteps,
            definition,
            [PipelineRunHelpers.SystemPrepareStage, "teardown"],
            [PipelineRunHelpers.SystemPrepareStage, "e2e", "teardown"]);

        Assert.Equal([PipelineRunHelpers.SystemCleanupStage], result);
    }

    [Fact]
    public void FindReadyStages_UserStageStillRunning_DoesNotUnlockSystemCleanup()
    {
        var definition = new PipelineYamlDefinition
        {
            Stages =
            [
                new PipelineStageDefinition { Name = "deploy" },
                new PipelineStageDefinition { Name = "teardown" }
            ]
        };
        var pendingSteps = new List<PipelineStepRun>
        {
            new() { StageName = PipelineRunHelpers.SystemCleanupStage, IsSystem = true }
        };

        var result = InvokeFindReadyStages(
            pendingSteps,
            definition,
            [PipelineRunHelpers.SystemPrepareStage],
            [PipelineRunHelpers.SystemPrepareStage, "deploy"]);

        Assert.Empty(result);
    }

    // --- MarkStepsAs ---

    [Fact]
    public void MarkStepsAs_SetsStatusAndTimestamp()
    {
        var steps = new List<PipelineStepRun>
        {
            new() { Status = TaskExecutionStatus.Pending },
            new() { Status = TaskExecutionStatus.Pending }
        };

        InvokeMarkStepsAs(steps, TaskExecutionStatus.Cancelled);

        Assert.All(steps, s => Assert.Equal(TaskExecutionStatus.Cancelled, s.Status));
        Assert.All(steps, s => Assert.NotNull(s.CompletedAt));
    }

    [Fact]
    public void MarkStepsAs_EmptyList_NoOp()
    {
        InvokeMarkStepsAs([], TaskExecutionStatus.Failed);
    }

    // --- ResolveLegVariables ---

    [Fact]
    public void ResolveLegVariables_EmptyLegKey_ReturnsCopyOfStageVars()
    {
        var stageVars = new Dictionary<string, string> { ["HOST"] = "localhost" };

        var result = InvokeResolveLegVariables(stageVars, "", []);

        Assert.Equal("localhost", result["HOST"]);
        stageVars["NEW"] = "val";
        Assert.False(result.ContainsKey("NEW"));
    }

    [Fact]
    public void ResolveLegVariables_MatchingLeg_MergesLegVars()
    {
        var stageVars = new Dictionary<string, string> { ["HOST"] = "localhost" };
        var matrixLegs = new List<Dictionary<string, string>>
        {
            new() { ["os"] = "ubuntu", ["arch"] = "x64" }
        };

        var result = InvokeResolveLegVariables(stageVars, "ubuntu-x64", matrixLegs);

        Assert.Equal("localhost", result["HOST"]);
        Assert.Equal("ubuntu", result["os"]);
        Assert.Equal("x64", result["arch"]);
    }

    [Fact]
    public void ResolveLegVariables_NoMatchingLeg_ReturnsBaseVarsOnly()
    {
        var stageVars = new Dictionary<string, string> { ["HOST"] = "localhost" };
        var matrixLegs = new List<Dictionary<string, string>>
        {
            new() { ["os"] = "ubuntu" }
        };

        var result = InvokeResolveLegVariables(stageVars, "windows", matrixLegs);

        Assert.Equal("localhost", result["HOST"]);
        Assert.False(result.ContainsKey("os"));
    }

    // --- HasNonContinuableFailures ---

    [Fact]
    public void HasNonContinuableFailures_EmptyList_ReturnsFalse()
    {
        Assert.False(InvokeHasNonContinuableFailures([]));
    }

    [Fact]
    public void HasNonContinuableFailures_AllContinuable_ReturnsFalse()
    {
        var steps = new List<PipelineStepRun>
        {
            new() { ContinueOnError = true },
            new() { ContinueOnError = true }
        };
        Assert.False(InvokeHasNonContinuableFailures(steps));
    }

    [Fact]
    public void HasNonContinuableFailures_OneNotContinuable_ReturnsTrue()
    {
        var steps = new List<PipelineStepRun>
        {
            new() { ContinueOnError = true },
            new() { ContinueOnError = false }
        };
        Assert.True(InvokeHasNonContinuableFailures(steps));
    }

    // Terminal status after every step settles.

    [Fact]
    public void DecideFinalStatus_IsPartial_WhenTheOnlyFailureWasSwallowed()
    {
        // A continuable non-advisory failure yields Partial rather than Success.
        Assert.Equal(PipelineStatus.Partial,
            InvokeDecideFinalStatus(cancelled: false, blockingFailure: false, swallowedFailure: true));
    }

    /// <summary>A run whose own assurance verdict refuses delivery finishes Rejected.</summary>
    [Fact]
    public void DecideFinalStatus_IsRejected_WhenTheRunRefusedItsResult_AndNothingBlockingFailed()
    {
        Assert.Equal(PipelineStatus.Rejected,
            InvokeDecideFinalStatus(cancelled: false, blockingFailure: false, swallowedFailure: true, rejected: true));
        Assert.Equal(PipelineStatus.Failed,
            InvokeDecideFinalStatus(cancelled: false, blockingFailure: true, swallowedFailure: false, rejected: true));
        Assert.Equal(PipelineStatus.Cancelled,
            InvokeDecideFinalStatus(cancelled: true, blockingFailure: false, swallowedFailure: false, rejected: true));
        Assert.True(PipelineStatus.Rejected.IsTerminal());
        Assert.True(PipelineStatus.Rejected.IsUnsuccessful());
    }

    [Fact]
    public void DecideFinalStatus_IsSuccess_WhenNothingFailed()
    {
        Assert.Equal(PipelineStatus.Success,
            InvokeDecideFinalStatus(cancelled: false, blockingFailure: false, swallowedFailure: false));
    }

    [Fact]
    public void DecideFinalStatus_IsFailed_WhenSomethingBlockingFailed()
    {
        // A blocking failure outranks a swallowed one: the run is red, not amber.
        Assert.Equal(PipelineStatus.Failed,
            InvokeDecideFinalStatus(cancelled: false, blockingFailure: true, swallowedFailure: true));
    }

    /// <summary>A failed deployment successfully compensated by its rollback stage finishes
    /// RolledBack.</summary>
    [Fact]
    public void DecideFinalStatus_IsRolledBack_WhenTheRollbackStageUndidTheFailure()
    {
        Assert.Equal(PipelineStatus.RolledBack,
            InvokeDecideFinalStatus(cancelled: false, blockingFailure: true, swallowedFailure: false, rolledBack: true));
        // No failure, nothing to roll back; a cancellation stays a cancellation.
        Assert.Equal(PipelineStatus.Success,
            InvokeDecideFinalStatus(cancelled: false, blockingFailure: false, swallowedFailure: false, rolledBack: true));
        Assert.Equal(PipelineStatus.Cancelled,
            InvokeDecideFinalStatus(cancelled: true, blockingFailure: true, swallowedFailure: false, rolledBack: true));
    }

    /// <summary>A rollback stage is a failure handler that runs a bluegreen-rollback step; a failure
    /// handler that only notifies must never turn a failed run into a rolled-back one.</summary>
    [Fact]
    public void RollbackStageNames_AreTheFailureHandlersThatRollBack()
    {
        var definition = new PipelineYamlDefinition
        {
            Name = "deploy",
            Stages =
            [
                new PipelineStageDefinition { Name = "Deploy", Steps = [new PipelineStepDefinition { Name = "up", Type = "bluegreen-up" }] },
                new PipelineStageDefinition { Name = "Rollback", Condition = "failed()", Steps = [new PipelineStepDefinition { Name = "restore", Type = "bluegreen-rollback" }] },
                new PipelineStageDefinition { Name = "Notify", Condition = "failed()", Steps = [new PipelineStepDefinition { Name = "mail", Shell = "echo" }] },
                new PipelineStageDefinition { Name = "Always", Condition = "always()", Steps = [new PipelineStepDefinition { Name = "restore", Type = "bluegreen-rollback" }] }
            ]
        };

        Assert.Equal(["Rollback"], PipelineDeploymentStages.RollbackNames(definition));
        // A rollback only counts after the new colour was started; 2383 failed before that.
        Assert.Equal(["Deploy"], PipelineDeploymentStages.StartNames(definition));
    }

    [Fact]
    public void DecideFinalStatus_IsCancelled_EvenWithFailuresRecorded()
    {
        Assert.Equal(PipelineStatus.Cancelled,
            InvokeDecideFinalStatus(cancelled: true, blockingFailure: true, swallowedFailure: true));
    }

    // --- Stage condition false → steps cancelled ---

    [Fact]
    public async Task AdvanceStageAsync_StageConditionFalse_CancelsSteps()
    {
        var yaml = """
            name: conditional
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: make
              - name: deploy
                agent: linux-01
                condition: "failed()"
                steps:
                  - name: ship
                    shell: deploy.sh
            """;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = yaml }
            });

        var deployStep = new PipelineStepRun { Id = 2, StageName = "deploy", StepName = "ship", PipelineRunId = 1 };
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([deployStep]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>())
            .Returns(false);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Cancelled, deployStep.Status);
        Assert.Equal("failed()", deployStep.SkippedCondition);
        Assert.Null(deployStep.SkippedConditionVariablesJson);
    }

    [Fact]
    public async Task AdvanceStageAsync_ConditionallySkippedStage_DispatchesItsDependentWithoutAnotherCallback()
    {
        var yaml = """
            name: conditional-chain
            trigger: manual
            stages:
              - name: source
                agent: linux-01
                steps:
                  - name: resolve
                    shell: git rev-parse HEAD
              - name: optional-artifact
                agent: linux-01
                depends_on: [source]
                condition: "eq(variables['USE_ARTIFACT'], 'true')"
                steps:
                  - name: restore
                    shell: restore.sh
              - name: deploy
                agent: linux-01
                depends_on: [optional-artifact]
                steps:
                  - name: ship
                    shell: deploy.sh
            """;

        var optional = new PipelineStepRun
        {
            Id = 2,
            StageName = "optional-artifact",
            StepName = "restore",
            PipelineRunId = 1
        };
        var deploy = new PipelineStepRun
        {
            Id = 3,
            StageName = "deploy",
            StepName = "ship",
            PipelineRunId = 1
        };
        var pendingCall = 0;
        var completedCall = 0;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "source", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "source", Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            AdditionalVariablesJson = "{\"USE_ARTIFACT\":\"false\"}",
            Pipeline = new Pipeline { YamlDefinition = yaml }
        });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => ++pendingCall == 1 ? [optional] : [deploy]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => ++completedCall == 1
                ? [PipelineRunHelpers.SystemPrepareStage, "source"]
                : [PipelineRunHelpers.SystemPrepareStage, "source", "optional-artifact"]);
        _repoMock.GetTerminalStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => [PipelineRunHelpers.SystemPrepareStage, "source", "optional-artifact"]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "source", TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Cancelled, optional.Status);
        Assert.Equal("eq(variables['USE_ARTIFACT'], 'true')", optional.SkippedCondition);
        var conditionVariables = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            Assert.IsType<string>(optional.SkippedConditionVariablesJson));
        Assert.Equal("false", conditionVariables!["USE_ARTIFACT"]);
        Assert.Equal(2, pendingCall);
        Assert.True(completedCall >= 2);
        await _repoMock.Received(1).FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>());
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "ship"));
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    // Aetheus-quality.yaml conditions each language producer at STEP level on the HAS_* variables
    // that detect-project-languages.sh exports from an earlier stage. This proves both halves on
    // the engine: the output of the earlier step reaches the step-level condition, and a false
    // condition skips only its own step while its sibling in the same stage is dispatched.
    [Fact]
    public async Task AdvanceStageAsync_StepConditionOnEarlierStepOutput_SkipsOnlyThatStep()
    {
        var yaml = """
            name: language-conditions
            trigger: manual
            stages:
              - name: Checkout
                agent: linux-01
                steps:
                  - name: Resolve source languages
                    shell: sh detect-project-languages.sh .
              - name: Lint
                agent: linux-01
                depends_on: [Checkout]
                steps:
                  - name: javascript
                    condition: "eq(variables['HAS_JAVASCRIPT'], 'true')"
                    shell: eslint .
                  - name: python
                    condition: "eq(variables['HAS_PYTHON'], 'true')"
                    shell: ruff check .
            """;

        var javascript = new PipelineStepRun { Id = 2, StageName = "Lint", StepName = "javascript", PipelineRunId = 1 };
        var python = new PipelineStepRun { Id = 3, StageName = "Lint", StepName = "python", PipelineRunId = 1 };

        _repoMock.AreAllStepsInStageCompletedAsync(1, "Checkout", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "Checkout", Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            Pipeline = new Pipeline { YamlDefinition = yaml }
        });
        _repoMock.GetSuccessfulStepOutputsAsync(1, Arg.Any<CancellationToken>())
            .Returns(new List<StepOutputProjection>
            {
                new("Checkout", "Resolve source languages", """{"HAS_JAVASCRIPT":"true","HAS_PYTHON":"false"}""")
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => [javascript, python]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "Checkout"]);
        _repoMock.GetTerminalStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "Checkout"]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "Checkout", TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Cancelled, python.Status);
        Assert.Equal("eq(variables['HAS_PYTHON'], 'true')", python.SkippedCondition);
        var conditionVariables = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            Assert.IsType<string>(python.SkippedConditionVariablesJson));
        Assert.Equal("false", conditionVariables!["HAS_PYTHON"]);
        Assert.Null(javascript.SkippedCondition);
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "javascript"));
        _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(task => task.Name == "python"));
    }

    // A condition the engine cannot read (here a compound it used to cut at its first eq) fails the
    // stage: it is never read as met (the steps would run) nor as not met (they would be skipped).
    [Fact]
    public async Task AdvanceStageAsync_StageConditionUnsupported_FailsTheStageStepsWithoutDispatching()
    {
        var yaml = """
            name: compound-stage
            trigger: manual
            stages:
              - name: source
                agent: linux-01
                steps:
                  - name: resolve
                    shell: git rev-parse HEAD
              - name: deploy
                agent: linux-01
                depends_on: [source]
                condition: "or(eq(variables['A'], 'x'), eq(variables['B'], 'y'))"
                steps:
                  - name: ship
                    shell: deploy.sh
            """;
        var ship = new PipelineStepRun { Id = 2, StageName = "deploy", StepName = "ship", PipelineRunId = 1 };
        var pendingCall = 0;

        _repoMock.AreAllStepsInStageCompletedAsync(1, "source", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "source", Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            // The first operand would have matched: the former engine dispatched this stage.
            AdditionalVariablesJson = "{\"A\":\"x\",\"B\":\"n\"}",
            Pipeline = new Pipeline { YamlDefinition = yaml }
        });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => ++pendingCall == 1 ? [ship] : []);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "source"]);
        _repoMock.GetTerminalStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "source"]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "source", TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, ship.Status);
        Assert.Equal(TaskFailureCodes.InfrastructureMismatch, ship.FailureCode);
        Assert.StartsWith("Stage 'deploy': condition 'or(", ship.FailureReason, StringComparison.Ordinal);
        Assert.Contains("is not supported", ship.FailureReason, StringComparison.Ordinal);
        Assert.Null(ship.SkippedCondition);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task AdvanceStageAsync_StepConditionWithTrailingText_FailsOnlyThatStep()
    {
        var yaml = """
            name: trailing-step
            trigger: manual
            stages:
              - name: Checkout
                agent: linux-01
                steps:
                  - name: resolve
                    shell: git rev-parse HEAD
              - name: Lint
                agent: linux-01
                depends_on: [Checkout]
                steps:
                  - name: javascript
                    condition: "eq(variables['HAS_JAVASCRIPT'], 'true')"
                    shell: eslint .
                  - name: python
                    condition: "eq(variables['HAS_PYTHON'], 'true') and ne(variables['SKIP'], 'true')"
                    shell: ruff check .
            """;
        var javascript = new PipelineStepRun { Id = 2, StageName = "Lint", StepName = "javascript", PipelineRunId = 1 };
        var python = new PipelineStepRun { Id = 3, StageName = "Lint", StepName = "python", PipelineRunId = 1 };

        _repoMock.AreAllStepsInStageCompletedAsync(1, "Checkout", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "Checkout", Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            AdditionalVariablesJson = "{\"HAS_JAVASCRIPT\":\"true\",\"HAS_PYTHON\":\"true\",\"SKIP\":\"true\"}",
            Pipeline = new Pipeline { YamlDefinition = yaml }
        });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns(_ => [javascript, python]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "Checkout"]);
        _repoMock.GetTerminalStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "Checkout"]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "Checkout", TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, python.Status);
        Assert.Equal(TaskFailureCodes.InfrastructureMismatch, python.FailureCode);
        Assert.StartsWith("Step 'python': condition 'eq(", python.FailureReason, StringComparison.Ordinal);
        Assert.Null(python.SkippedCondition);
        _repoMock.DidNotReceive().TrackTask(Arg.Is<ServerTask>(task => task.Name == "python"));
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "javascript"));
    }

    [Fact]
    public async Task ReconcileRunAsync_ReadyPendingStage_DispatchesWithoutCompletionCallback()
    {
        var yaml = """
            name: release
            trigger: manual
            stages:
              - name: source
                agent: linux-01
                steps:
                  - name: resolve
                    shell: git rev-parse HEAD
              - name: CI
                agent: linux-01
                depends_on: [source]
                steps:
                  - name: launch-ci
                    shell: ci.sh
            """;
        var ciStep = new PipelineStepRun
        {
            Id = 3,
            StageName = "CI",
            StepName = "launch-ci",
            PipelineRunId = 220
        };
        var run = new PipelineRun
        {
            Id = 220,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { Id = 1, YamlDefinition = yaml }
        };

        _repoMock.GetPipelineRunWithPipelineAsync(220, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPendingStepRunsAsync(220, Arg.Any<CancellationToken>()).Returns([ciStep]);
        _repoMock.GetCompletedStageNamesAsync(220, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "source"]);
        _repoMock.GetTerminalStageNamesAsync(220, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "source"]);
        _repoMock.HasAnyFailedStepInRunAsync(220, Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.FindOnlineServerByAgentAsync("linux-01", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "linux-01" });
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.ReconcileRunAsync(220, TestContext.Current.CancellationToken);

        await _operationLockMock.Received(1).RunSerializedAsync(
            "pipeline-run-advance:220",
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task => task.Name == "launch-ci"));
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(220, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileRunAsync_ActiveArtifactCollection_BlocksConsumerDispatch()
    {
        const string yaml = """
            name: release
            stages:
              - name: source
                artifacts: [out/**]
                steps:
                  - name: build
                    shell: build.sh
              - name: consume
                depends_on: [source]
                steps:
                  - name: consume-artifact
                    shell: consume.sh
            """;
        var run = new PipelineRun
        {
            Id = 221,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { Id = 1, YamlDefinition = yaml }
        };
        _repoMock.GetPipelineRunWithPipelineAsync(221, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPendingStepRunsAsync(221, Arg.Any<CancellationToken>())
            .Returns([
                new PipelineStepRun
                {
                    Id = 3,
                    StageName = "consume",
                    StepName = "consume-artifact",
                    PipelineRunId = 221
                }
            ]);
        _repoMock.HasActiveArtifactCollectionAsync(221, Arg.Any<CancellationToken>())
            .Returns(true);

        await _sut.ReconcileRunAsync(221, TestContext.Current.CancellationToken);

        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
        await _repoMock.DidNotReceive().GetCompletedStageNamesAsync(
            221,
            Arg.Any<CancellationToken>());
    }

    // --- No server matched → run fails ---

    [Fact]
    public async Task ReconcileRunAsync_ConfiguredRunnerTemporarilyOffline_KeepsPreparePendingThenDispatches()
    {
        var yaml = """
            name: ci
            trigger: manual
            stages:
              - name: Compile
                os: linux
                steps:
                  - name: build
                    shell: dotnet build
            """;
        var prepare = new PipelineStepRun
        {
            Id = 1,
            PipelineRunId = 1,
            StageName = PipelineRunHelpers.SystemPrepareStage,
            StepName = "Clone Repository",
            Status = TaskExecutionStatus.Pending,
            IsSystem = true
        };
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline { Id = 1, YamlDefinition = yaml }
        };
        var onlineRunner = new Server { Id = 10, Name = "linux-01", OsType = OsType.Linux };

        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([prepare]);
        _repoMock.FindAnyOnlineRunnerAsync(Arg.Any<int?>(), OsType.Linux, Arg.Any<CancellationToken>())
            .Returns((Server?)null, onlineRunner);
        _repoMock.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), OsType.Linux,
                Arg.Any<int?>(), false, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([10]);

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Pending, prepare.Status);
        _repoMock.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(
            1, PipelineStatus.Failed, Arg.Any<CancellationToken>());

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Assigned, prepare.Status);
        _repoMock.Received(1).TrackTask(Arg.Is<ServerTask>(task =>
            task.PipelineRunId == 1 && task.PipelineStepRunId == prepare.Id && task.ServerId == onlineRunner.Id));
    }

    [Fact]
    public async Task ReconcileRunAsync_ExternalRepositorySnapshot_DispatchesCloneWithoutInternalCredentials()
    {
        var yaml = """
            name: ci
            trigger: manual
            stages:
              - name: Compile
                os: linux
                steps:
                  - name: build
                    shell: dotnet build
            """;
        var prepare = new PipelineStepRun
        {
            Id = 1,
            PipelineRunId = 1,
            StageName = PipelineRunHelpers.SystemPrepareStage,
            StepName = "Clone Repository",
            Status = TaskExecutionStatus.Pending,
            IsSystem = true
        };
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = PipelineStatus.Running,
            RepositoryUrl = "https://git.example.test/1/toto.git",
            ResolvedVariablesJson = $$"""
                {
                  "REPOSITORY_URL": "https://git.example.test/1/toto.git",
                  "BUILD_SOURCEVERSION": "{{DefaultCommit}}"
                }
                """,
            Pipeline = new Pipeline { Id = 1, ProjectId = 7, YamlDefinition = yaml }
        };
        var onlineRunner = new Server { Id = 10, Name = "linux-01", OsType = OsType.Linux };
        ServerTask? dispatched = null;

        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([prepare]);
        _repoMock.FindAnyOnlineRunnerAsync(Arg.Any<int?>(), OsType.Linux, Arg.Any<CancellationToken>())
            .Returns(onlineRunner);
        _repoMock.TrackTask(Arg.Do<ServerTask>(task => dispatched = task));
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.ReconcileRunAsync(1, TestContext.Current.CancellationToken);

        Assert.NotNull(dispatched);
        Assert.Contains("git init", dispatched.Command, StringComparison.Ordinal);
        Assert.Contains("git fetch", dispatched.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("GIT_USERNAME", dispatched.EnvironmentVariables, StringComparison.Ordinal);
        Assert.DoesNotContain("GIT_PASSWORD", dispatched.EnvironmentVariables, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdvanceStageAsync_NoServerMatched_FailsRun()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: make
              - name: deploy
                agent: nonexistent-agent
                steps:
                  - name: ship
                    shell: deploy.sh
            """;
        // This fleet has no server carrying that agent name: the stage is unmatchable, not merely
        // offline, which is the distinction the planner acts on below.
        _repoMock.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), "nonexistent-agent", Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<int>());

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun { Id = 2, StageName = "deploy", StepName = "ship", PipelineRunId = 1 }]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>())
            .Returns(false);
        _repoMock.FindOnlineServerByAgentAsync("nonexistent-agent", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns((Server?)null);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_NoServerMatched_PersistsReasonViaTrackedRepoCall()
    {
        // Regression: the "no online server" reason must be persisted through the tracked
        // AppendRunWarningsAsync repo call. The previous code mutated the AsNoTracking entity
        // returned by GetPipelineRunWithPipelineAsync, so the run failed with no visible message.
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: make
              - name: deploy
                agent: nonexistent-agent
                steps:
                  - name: ship
                    shell: deploy.sh
            """;
        // Same fleet premise as the sibling test: nothing is configured for that agent name.
        _repoMock.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), "nonexistent-agent", Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<int>());

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = yaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun { Id = 2, StageName = "deploy", StepName = "ship", PipelineRunId = 1 }]);
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.HasAnyFailedStepInRunAsync(1, Arg.Any<CancellationToken>())
            .Returns(false);
        _repoMock.FindOnlineServerByAgentAsync("nonexistent-agent", Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns((Server?)null);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.AppendRunWarningsAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "build", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AppendRunWarningsAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(w => w.Any(m => m.Contains("nonexistent-agent"))),
            Arg.Any<CancellationToken>());
    }

    // --- Dead-end runs fail explicitly instead of hanging in Running ---

    private const string DeadlockYaml = """
        name: dl
        trigger: manual
        stages:
          - name: A
            agent: x
            steps:
              - name: s1
                shell: echo a
          - name: B
            agent: x
            depends_on: [A]
            steps:
              - name: s2
                shell: echo b
        """;

    [Fact]
    public async Task AdvanceStageAsync_DeadlockedStage_FailsRunWithReason()
    {
        _repoMock.AreAllStepsInStageCompletedAsync(1, "A", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "A", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Running,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = DeadlockYaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun { Id = 2, StageName = "B", StepName = "s2", PipelineRunId = 1 }]);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.AppendRunWarningsAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "A", ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AppendRunWarningsAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(w => w.Any(m => m.Contains("depends on 'A'"))),
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeAfterApprovalAsync_NoExecutableSteps_FailsRunWithReason()
    {
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(
                new PipelineRun
                {
                    Id = 1,
                    PipelineId = 1,
                    Status = PipelineStatus.WaitingForApproval,
                    ResolvedVariablesJson = "{}",
                    Pipeline = new Pipeline { YamlDefinition = DeadlockYaml }
                },
                new PipelineRun
                {
                    Id = 1,
                    PipelineId = 1,
                    Status = PipelineStatus.Running,
                    ResolvedVariablesJson = "{}",
                    Pipeline = new Pipeline { YamlDefinition = DeadlockYaml }
                });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.TryTransitionPipelineRunStatusAsync(
                1, PipelineStatus.WaitingForApproval, PipelineStatus.Running, Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.AppendRunWarningsAsync(1, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.UpdatePipelineRunStatusAsync(1, Arg.Any<PipelineStatus>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ResumeAfterApprovalAsync(1, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AppendRunWarningsAsync(
            1,
            Arg.Is<IReadOnlyCollection<string>>(w => w.Any(m => m.Contains("no executable steps"))),
            Arg.Any<CancellationToken>());
        await _repoMock.Received(1).UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResumeAfterApprovalAsync_WaitsForTheDispatchPassHoldingTheRunLock()
    {
        // The resume re-dispatched the run's next stage without the per-run lock every other dispatch
        // pass takes, so it could create the same stage's tasks next to a completion or reconcile pass.
        const int runId = 7;
        var run = new PipelineRun
        {
            Id = runId,
            PipelineId = 1,
            Status = PipelineStatus.WaitingForApproval,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline
            {
                Id = 1,
                YamlDefinition = """
                    name: p
                    stages:
                      - name: build
                        steps:
                          - name: compile
                            shell: dotnet build
                    """
            }
        };
        _repoMock.GetPipelineRunWithPipelineAsync(runId, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.TryTransitionPipelineRunStatusAsync(
                runId, PipelineStatus.WaitingForApproval, PipelineStatus.Running, Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetPendingStepRunsAsync(runId, Arg.Any<CancellationToken>()).Returns([]);
        var passInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePass = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _repoMock.IsRunStillRunningAsync(runId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            passInside.TrySetResult();
            return releasePass.Task;
        });

        // A reconcile pass takes the run lock and is held inside it.
        var reconcile = _sut.ReconcileRunAsync(runId, TestContext.Current.CancellationToken);
        await passInside.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var resume = _sut.ResumeAfterApprovalAsync(runId, TestContext.Current.CancellationToken);

        Assert.False(resume.IsCompleted);
        await _repoMock.DidNotReceive().GetPendingStepRunsAsync(runId, Arg.Any<CancellationToken>());

        releasePass.SetResult(false);
        await reconcile.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(await resume.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await _repoMock.Received().GetPendingStepRunsAsync(runId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryFailedStepsAsync_WaitsForTheDispatchPassHoldingTheRunLock()
    {
        // Same race as the resume: the retry re-opens the run, then dispatched outside the run lock.
        const int runId = 8;
        var run = new PipelineRun
        {
            Id = runId,
            PipelineId = 1,
            Status = PipelineStatus.Failed,
            ResolvedVariablesJson = "{}",
            Pipeline = new Pipeline
            {
                Id = 1,
                YamlDefinition = """
                    name: p
                    stages:
                      - name: build
                        steps:
                          - name: compile
                            shell: dotnet build
                    """
            }
        };
        _repoMock.GetPipelineRunWithPipelineAsync(runId, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.ResetFailedStepRunsAsync(runId, Arg.Any<CancellationToken>()).Returns(1);
        _repoMock.GetPendingStepRunsAsync(runId, Arg.Any<CancellationToken>()).Returns([]);
        var passInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePass = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _repoMock.IsRunStillRunningAsync(runId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            passInside.TrySetResult();
            return releasePass.Task;
        });

        var reconcile = _sut.ReconcileRunAsync(runId, TestContext.Current.CancellationToken);
        await passInside.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var retry = _sut.RetryFailedStepsAsync(runId, TestContext.Current.CancellationToken);

        Assert.False(retry.IsCompleted);
        await _repoMock.DidNotReceive().GetPendingStepRunsAsync(runId, Arg.Any<CancellationToken>());

        releasePass.SetResult(false);
        await reconcile.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await retry.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await _repoMock.Received().GetPendingStepRunsAsync(runId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdvanceStageAsync_DeadlockButRunNotRunning_DoesNotFlipStatus()
    {
        _repoMock.AreAllStepsInStageCompletedAsync(1, "A", Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "A", Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Cancelled,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = DeadlockYaml }
            });
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new PipelineStepRun { Id = 2, StageName = "B", StepName = "s2", PipelineRunId = 1 }]);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.AdvanceStageAsync(1, "A", ct: TestContext.Current.CancellationToken);

        await _repoMock.DidNotReceive().UpdatePipelineRunStatusAsync(1, PipelineStatus.Failed, Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().AppendRunWarningsAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    // --- RetryFailedStepsAsync (partial re-run) ---

    [Fact]
    public async Task RetryFailedStepsAsync_RunNotFound_ReturnsNull()
    {
        _repoMock.GetPipelineRunWithPipelineAsync(99, Arg.Any<CancellationToken>())
            .Returns((PipelineRun?)null);

        Assert.Null(await _sut.RetryFailedStepsAsync(99, ct: TestContext.Current.CancellationToken));
        await _repoMock.DidNotReceive().ResetFailedStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryFailedStepsAsync_RunNotFailed_SaysSoInsteadOfReturningNull()
    {
        // Retry refusal returns a specific reason rather than appearing to be another execution
        // failure.
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 1, PipelineId = 1, Status = PipelineStatus.Running, Pipeline = new Pipeline() });

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => _sut.RetryFailedStepsAsync(1, ct: TestContext.Current.CancellationToken));

        Assert.Contains("Running", ex.Message, StringComparison.Ordinal);
        await _repoMock.DidNotReceive().ResetFailedStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryFailedStepsAsync_NoFailedSteps_PointsAtRerunForAnEditedDefinition()
    {
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Failed,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { YamlDefinition = "name: x" }
            });
        _repoMock.ResetFailedStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns(0);

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => _sut.RetryFailedStepsAsync(1, ct: TestContext.Current.CancellationToken));

        // A run replays the YAML captured when it was triggered (ADR-015), so retrying after editing the
        // definition replays the old one. The message has to say which action does pick the edit up.
        Assert.Contains("Re-run", ex.Message, StringComparison.Ordinal);
        await _repoMock.DidNotReceive().GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(PipelineStatus.Failed)]
    [InlineData(PipelineStatus.Cancelled)]
    public async Task RetryFailedStepsAsync_RetriableRun_ResetsAndReturnsRun(PipelineStatus status)
    {
        var yaml = """
            name: x
            trigger: manual
            stages:
              - name: s
                agent: a
                steps:
                  - name: st
                    shell: echo hi
            """;
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = status,
                ResolvedVariablesJson = "{}",
                Pipeline = new Pipeline { Id = 1, YamlDefinition = yaml }
            });
        _repoMock.ResetFailedStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns(1);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetRunDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun
            {
                Id = 1,
                PipelineId = 1,
                Status = PipelineStatus.Running,
                Pipeline = new Pipeline { Name = "CI" },
                StepRuns = [new PipelineStepRun { Id = 1, StepName = "st", StageName = "s" }]
            });

        var result = await _sut.RetryFailedStepsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("CI", result.PipelineName);
        await _repoMock.Received(1).ResetFailedStepRunsAsync(1, Arg.Any<CancellationToken>());
    }

    // --- RerunAsync (G: rerun modes) ---

    private const string RerunSnapshotYaml = """
        name: snapshot
        trigger: manual
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: compile
                shell: dotnet build
        """;

    [Fact]
    public async Task RerunAsync_SourceRunNotFound_ReturnsNull()
    {
        _repoMock.GetRunDetailAsync(99, Arg.Any<CancellationToken>()).Returns((PipelineRun?)null);

        Assert.Null(await _sut.RerunAsync(99, RerunMode.Current, ct: TestContext.Current.CancellationToken));
        await _repoMock.DidNotReceive().AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RerunAsync_SnapshotSameCommit_ReplaysSnapshotYamlPinnedToCommit()
    {
        var source = new PipelineRun
        {
            Id = 5,
            PipelineId = 1,
            Status = PipelineStatus.Success,
            YamlSnapshot = RerunSnapshotYaml,
            CommitHash = DefaultCommit,
            AdditionalVariablesJson = "{}",
            Pipeline = new Pipeline { Name = "Deploy" },
            StepRuns = []
        };
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(source);
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = "name: live\ntrigger: manual\nstages: []", Runs = [] });
        _repoMock.AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.RerunAsync(5, RerunMode.SnapshotSameCommit, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _repoMock.Received(1).AddPipelineRunAsync(
            Arg.Is<PipelineRun>(r => r.YamlSnapshot == RerunSnapshotYaml && r.CommitHash == DefaultCommit),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RerunAsync_SnapshotSameCommit_OfASourceRun_ReadsTheDefinitionAtItsRevision_AndPinsTheWorkspace()
    {
        // Same-commit reruns preserve separate definition and workspace revisions when a source
        // block selects another repository.
        const string definitionCommit = "0123456789abcdef0123456789abcdef01234567";
        const string workspaceCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string yaml = "name: nightly-public\ntrigger: manual\nsource_branch: develop\nsource:\n  repository: aetheus-public\nstages: []";
        _repoMock.GetRunDetailAsync(5, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 5,
            PipelineId = 1,
            Status = PipelineStatus.Failed,
            YamlSnapshot = yaml,
            BranchName = "main",
            CommitHash = workspaceCommit,
            AdditionalVariablesJson = $"{{\"AETHEUS_DEFINITION_COMMIT\":\"{definitionCommit}\",\"AETHEUS_DEFINITION_BRANCH\":\"develop\"}}",
            Pipeline = new Pipeline { Name = "nightly-public" },
            StepRuns = []
        });
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(new Pipeline
        {
            Id = 1,
            Name = "nightly-public",
            ProjectId = 7,
            SourceRepositoryId = 11,
            YamlDefinition = yaml,
            Runs = []
        });
        _pipelineGitMock.GetPipelineSourceAsync(7, "nightly-public", Arg.Any<CancellationToken>(), Arg.Any<string?>(), 11)
            .Returns(new PipelineSourceDto
            {
                RepositoryId = 11,
                CloneUrl = "https://git.example.test/git/7/aetheus.git",
                Branch = "develop",
                CommitHash = definitionCommit
            });
        _workspaceSourcesMock.ResolveAsync(
                7, Arg.Any<PipelineSourceDefinition>(), 11, definitionCommit, Arg.Any<CancellationToken>(), workspaceCommit)
            .Returns(new PipelineWorkspaceSource(20, "https://git.example.test/git/7/aetheus-public.git", "main", workspaceCommit));
        PipelineRun? captured = null;
        _repoMock.AddPipelineRunAsync(Arg.Do<PipelineRun>(run => captured = run), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        await _sut.RerunAsync(5, RerunMode.SnapshotSameCommit, ct: TestContext.Current.CancellationToken);

        await _workspaceSourcesMock.Received(1).ResolveAsync(
            7, Arg.Any<PipelineSourceDefinition>(), 11, definitionCommit, Arg.Any<CancellationToken>(), workspaceCommit);
        Assert.NotNull(captured);
        Assert.Equal(workspaceCommit, captured!.CommitHash);
        Assert.Equal(definitionCommit, PipelineRunService.ResolveDefinitionCommit(captured));
    }

    [Fact]
    public async Task RerunAsync_Current_StartsFreshRunFromLiveDefinition()
    {
        const string liveYaml = """
            name: live
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;
        var source = new PipelineRun
        {
            Id = 5,
            PipelineId = 1,
            Status = PipelineStatus.Failed,
            YamlSnapshot = RerunSnapshotYaml,
            CommitHash = "abc123",
            AdditionalVariablesJson = "{}",
            Pipeline = new Pipeline { Name = "Deploy" },
            StepRuns = []
        };
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(source);
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = liveYaml, Runs = [] });
        _repoMock.AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.RerunAsync(5, RerunMode.Current, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _repoMock.Received(1).AddPipelineRunAsync(
            Arg.Is<PipelineRun>(r => r.YamlSnapshot == liveYaml),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RerunAsync_ResumeCheckpoints_PinsSnapshotCommitAndSourceRun()
    {
        var source = new PipelineRun
        {
            Id = 5,
            PipelineId = 1,
            Status = PipelineStatus.Failed,
            YamlSnapshot = RerunSnapshotYaml,
            CommitHash = DefaultCommit,
            AdditionalVariablesJson = "{}",
            ParametersJson = "{}",
            Pipeline = new Pipeline { Name = "aetheus-candidate" },
            StepRuns = []
        };
        _repoMock.GetRunDetailAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(source);
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "aetheus-candidate", YamlDefinition = RerunSnapshotYaml, Runs = [] });
        _repoMock.AddPipelineRunAsync(Arg.Any<PipelineRun>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetPendingStepRunsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.RerunAsync(5, RerunMode.ResumeCheckpoints, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _repoMock.Received(1).AddPipelineRunAsync(
            Arg.Is<PipelineRun>(run => run.YamlSnapshot == RerunSnapshotYaml
                && run.CommitHash == DefaultCommit
                && PipelineRunService.DeserializeResolvedVariablesStatic(run.AdditionalVariablesJson)
                    .GetValueOrDefault(PipelineRunService.ResumeSourceRunVariable) == "5"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCheckpointResumePreview_ListsReusableRunIdsAndMandatoryReplays()
    {
        var source = new PipelineRun { Id = 5, PipelineId = 1, Pipeline = new Pipeline { Name = "aetheus-candidate" } };
        _repoMock.GetRunDetailAsync(5, Arg.Any<CancellationToken>()).Returns(source);
        _repoMock.FindTriggeredRunIdByPipelineNameAsync(5, "aetheus-ci", Arg.Any<CancellationToken>()).Returns(8);
        _repoMock.GetRunDetailAsync(8, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 8,
            Status = PipelineStatus.Success,
            StepRuns =
            [
                new PipelineStepRun
                {
                    Task = new ServerTask { AssignedAgentVersion = "1.2.3" }
                }
            ],
            Pipeline = new Pipeline { Name = "aetheus-ci" }
        });
        _artifactRepoMock.GetByRunAsync(8, Arg.Any<CancellationToken>()).Returns(
        [
            new PipelineArtifact { Sha256 = new string('a', 64) }
        ]);

        var preview = await _sut.GetCheckpointResumePreviewAsync(5, TestContext.Current.CancellationToken);

        Assert.NotNull(preview);
        Assert.Contains(preview.Items, item => item.PipelineName == "aetheus-ci"
            && item.RunId == 8 && item.ReuseCandidate);
        Assert.Equal(2, preview.Items.Count(item => !item.ReuseCandidate && item.RunId is null));
    }

    [Fact]
    public async Task GetCheckpointResumePreview_ChildWithoutAgentTask_IsNeverReusable()
    {
        _repoMock.GetRunDetailAsync(5, Arg.Any<CancellationToken>()).Returns(
            new PipelineRun { Id = 5, PipelineId = 1, Pipeline = new Pipeline { Name = "aetheus-candidate" } });
        _repoMock.FindTriggeredRunIdByPipelineNameAsync(5, "aetheus-ci", Arg.Any<CancellationToken>()).Returns(8);
        _repoMock.GetRunDetailAsync(8, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 8,
            Status = PipelineStatus.Success,
            StepRuns = [],
            Pipeline = new Pipeline { Name = "aetheus-ci" }
        });
        _artifactRepoMock.GetByRunAsync(8, Arg.Any<CancellationToken>()).Returns(
        [
            new PipelineArtifact { Sha256 = new string('a', 64) }
        ]);

        var preview = await _sut.GetCheckpointResumePreviewAsync(5, TestContext.Current.CancellationToken);

        Assert.NotNull(preview);
        Assert.Contains(preview.Items, item => item.PipelineName == "aetheus-ci"
            && item.RunId == 8 && !item.ReuseCandidate);
    }

    [Theory]
    [InlineData("aetheus-ci")]
    [InlineData("aetheus-quality")]
    [InlineData("aetheus-security")]
    public async Task TryReuseCheckpoint_ValidContractsAndBytes_ReusesAndLeasesCheckpoint(string targetName)
    {
        var fixture = await ArrangeCheckpointReuseAsync(targetName: targetName);

        var reused = await _checkpoints.TryReuseCheckpointAsync(
            fixture.CurrentParent, fixture.Target, fixture.Target.Name, fixture.Preparation,
            new Dictionary<string, string>(), fixture.TriggerStep, fixture.Now,
            TestContext.Current.CancellationToken);

        Assert.True(reused);
        Assert.Equal(TaskExecutionStatus.Success, fixture.TriggerStep.Status);
        Assert.Equal(fixture.Checkpoint.Id, fixture.TriggerStep.TriggeredRunId);
        Assert.True(fixture.Artifact.RetentionLeaseExpiresAt >= fixture.Now.AddHours(24));
        await _artifactRepoMock.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _operationLockMock.Received(1).RunSerializedAsync(
            ArtifactRetentionLock.For(fixture.Artifact.Id),
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync(
            "PipelineCheckpointReused", "PipelineRun", fixture.CurrentParent.Id,
            Arg.Is<string>(value => value.Contains($"checkpoint={fixture.Checkpoint.Id}", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryReuseCheckpoint_AlteredArtifactBytes_Replays()
    {
        var fixture = await ArrangeCheckpointReuseAsync();
        var alteredBytes = Enumerable.Repeat((byte)'x', checked((int)fixture.Artifact.SizeBytes)).ToArray();
        _artifactStorageMock.OpenArtifact(fixture.Artifact.FilePath)
            .Returns(_ => new MemoryStream(alteredBytes));

        var reused = await _checkpoints.TryReuseCheckpointAsync(
            fixture.CurrentParent, fixture.Target, fixture.Target.Name, fixture.Preparation,
            new Dictionary<string, string>(), fixture.TriggerStep, fixture.Now,
            TestContext.Current.CancellationToken);

        Assert.False(reused);
        Assert.Null(fixture.Artifact.RetentionLeaseExpiresAt);
        Assert.Equal(TaskExecutionStatus.Pending, fixture.TriggerStep.Status);
    }

    [Theory]
    [InlineData(PipelineStatus.Failed)]
    [InlineData(PipelineStatus.Running)]
    public async Task TryReuseCheckpoint_NonSuccessChild_Replays(PipelineStatus status)
    {
        var fixture = await ArrangeCheckpointReuseAsync();
        fixture.Checkpoint.Status = status;

        var reused = await _checkpoints.TryReuseCheckpointAsync(
            fixture.CurrentParent, fixture.Target, fixture.Target.Name, fixture.Preparation,
            new Dictionary<string, string>(), fixture.TriggerStep, fixture.Now,
            TestContext.Current.CancellationToken);

        Assert.False(reused);
        _artifactStorageMock.DidNotReceive().OpenArtifact(Arg.Any<string>());
    }

    [Theory]
    [InlineData("yaml")]
    [InlineData("parameters")]
    public async Task TryReuseCheckpoint_DefinitionOrParameterDrift_Replays(string drift)
    {
        var fixture = await ArrangeCheckpointReuseAsync();
        if (drift == "yaml") fixture.Checkpoint.YamlSnapshot += "\n# drift";
        else fixture.Checkpoint.ParametersJson = "{\"mode\":\"different\"}";

        var reused = await _checkpoints.TryReuseCheckpointAsync(
            fixture.CurrentParent, fixture.Target, fixture.Target.Name, fixture.Preparation,
            new Dictionary<string, string>(), fixture.TriggerStep, fixture.Now,
            TestContext.Current.CancellationToken);

        Assert.False(reused);
        _artifactStorageMock.DidNotReceive().OpenArtifact(Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryReuseCheckpoint_AgentOrScannerContractDrift_Replays(bool scanner)
    {
        var fixture = await ArrangeCheckpointReuseAsync(scannerTask: scanner);
        if (scanner)
            fixture.Server.ScannerCapabilitiesJson = JsonSerializer.Serialize(
                new[] { $"scanner-manifest:sha256:{new string('b', 64)}" });
        else
            fixture.Server.AgentVersion = "9.9.9";

        var reused = await _checkpoints.TryReuseCheckpointAsync(
            fixture.CurrentParent, fixture.Target, fixture.Target.Name, fixture.Preparation,
            new Dictionary<string, string>(), fixture.TriggerStep, fixture.Now,
            TestContext.Current.CancellationToken);

        Assert.False(reused);
        _artifactStorageMock.DidNotReceive().OpenArtifact(Arg.Any<string>());
    }

    [Fact]
    public async Task TryReuseCheckpoint_ChildWithoutAgentTask_Replays()
    {
        var fixture = await ArrangeCheckpointReuseAsync();
        fixture.Checkpoint.StepRuns.Clear();

        var reused = await _checkpoints.TryReuseCheckpointAsync(
            fixture.CurrentParent, fixture.Target, fixture.Target.Name, fixture.Preparation,
            new Dictionary<string, string>(), fixture.TriggerStep, fixture.Now,
            TestContext.Current.CancellationToken);

        Assert.False(reused);
        _artifactStorageMock.DidNotReceive().OpenArtifact(Arg.Any<string>());
    }

    /// <summary>A wizard-created project names its children after itself; the role comes from the
    /// shipped template each one extends.</summary>
    [Theory]
    [InlineData("portfolio-ci", "application-ci", "AETHEUS_CHECKPOINT_CI_RUN_ID")]
    [InlineData("portfolio-quality", "application-quality", "AETHEUS_CHECKPOINT_QUALITY_RUN_ID")]
    [InlineData("portfolio-security", "application-security", "AETHEUS_CHECKPOINT_SECURITY_RUN_ID")]
    public async Task TryReuseCheckpoint_ChildOfAnyProject_ReusesByTheTemplateItExtends(
        string targetName, string template, string checkpointVariable)
    {
        var fixture = await ArrangeCheckpointReuseAsync(targetName: targetName, templateReference: template);

        var reused = await _checkpoints.TryReuseCheckpointAsync(
            fixture.CurrentParent, fixture.Target, fixture.Target.Name, fixture.Preparation,
            new Dictionary<string, string>(), fixture.TriggerStep, fixture.Now,
            TestContext.Current.CancellationToken);

        Assert.True(reused);
        Assert.Equal(fixture.Checkpoint.Id.ToString(), PipelineRunService.DeserializeResolvedVariablesStatic(
            fixture.TriggerStep.OutputVariablesJson)[checkpointVariable]);
    }

    [Fact]
    public async Task TryReuseCheckpoint_ChildExtendingNoResumableTemplate_AlwaysReplays()
    {
        var fixture = await ArrangeCheckpointReuseAsync(targetName: "portfolio-qa", templateReference: "application-qa");

        var reused = await _checkpoints.TryReuseCheckpointAsync(
            fixture.CurrentParent, fixture.Target, fixture.Target.Name, fixture.Preparation,
            new Dictionary<string, string>(), fixture.TriggerStep, fixture.Now,
            TestContext.Current.CancellationToken);

        Assert.False(reused);
    }

    [Fact]
    public async Task GetCheckpointResumePreview_NamesTheChildrenTheCandidateItselfNames()
    {
        _repoMock.GetRunDetailAsync(5, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 5,
            PipelineId = 1,
            Pipeline = new Pipeline { Name = "portfolio-candidate" },
            ResolvedVariablesJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["APPLICATION_CI_PIPELINE"] = "portfolio-ci",
                ["APPLICATION_QUALITY_PIPELINE"] = "portfolio-quality",
                ["APPLICATION_SECURITY_PIPELINE"] = "portfolio-security"
            })
        });
        _repoMock.FindTriggeredRunIdByPipelineNameAsync(5, "portfolio-ci", Arg.Any<CancellationToken>()).Returns(8);
        _repoMock.GetRunDetailAsync(8, Arg.Any<CancellationToken>()).Returns(new PipelineRun
        {
            Id = 8,
            Status = PipelineStatus.Success,
            StepRuns = [new PipelineStepRun { Task = new ServerTask { AssignedAgentVersion = "1.2.3" } }],
            Pipeline = new Pipeline { Name = "portfolio-ci" }
        });
        _artifactRepoMock.GetByRunAsync(8, Arg.Any<CancellationToken>()).Returns(
            [new PipelineArtifact { Sha256 = new string('a', 64) }]);

        var preview = await _sut.GetCheckpointResumePreviewAsync(5, TestContext.Current.CancellationToken);

        Assert.NotNull(preview);
        Assert.Equal(["portfolio-ci", "portfolio-quality", "portfolio-security"],
            preview.Items.Select(item => item.PipelineName));
        Assert.Contains(preview.Items, item => item.PipelineName == "portfolio-ci" && item.RunId == 8 && item.ReuseCandidate);
    }

    [Fact]
    public async Task TryReuseCheckpoint_QaAlwaysReplaysEvenWithValidEvidence()
    {
        var fixture = await ArrangeCheckpointReuseAsync(targetName: "aetheus-qa");

        var reused = await _checkpoints.TryReuseCheckpointAsync(
            fixture.CurrentParent, fixture.Target, fixture.Target.Name, fixture.Preparation,
            new Dictionary<string, string>(), fixture.TriggerStep, fixture.Now,
            TestContext.Current.CancellationToken);

        Assert.False(reused);
        _artifactStorageMock.DidNotReceive().OpenArtifact(Arg.Any<string>());
    }

    private async Task<CheckpointReuseFixture> ArrangeCheckpointReuseAsync(
        bool scannerTask = false,
        string targetName = "aetheus-ci",
        string? templateReference = null)
    {
        const int sourceParentRunId = 100;
        const int checkpointRunId = 200;
        var yaml = $$"""
            name: {{targetName}}
            stages:
              - name: Build
                steps:
                  - name: compile
                    shell: echo ok
            """;
        var definition = YamlParsingHelper.ParseAndValidate(yaml, _loggerMock)!;
        var target = new Pipeline
        {
            Id = 2,
            ProjectId = 7,
            Name = targetName,
            YamlDefinition = yaml,
            TemplateReferenceName = templateReference
        };
        var preparation = new PipelineRunPreparation
        {
            PipelineId = target.Id,
            Pipeline = target,
            CommitHash = DefaultCommit,
            YamlSnapshot = yaml,
            Definition = definition,
            EffectiveStages = YamlParsingHelper.FlattenJobs(definition),
            EffectiveProjectId = 7,
            TargetServerIds = []
        };
        _repoMock.GetPipelineOrganizationIdAsync(target.Id, Arg.Any<CancellationToken>()).Returns(1);
        preparation = await _sut.ResolveRunParametersAsync(
            preparation, new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        var sourceParent = new PipelineRun
        {
            Id = sourceParentRunId,
            PipelineId = 1,
            CommitHash = DefaultCommit,
            ParametersJson = "{}",
            Pipeline = new Pipeline { Id = 1, Name = "aetheus-candidate" }
        };
        var currentParent = new PipelineRun
        {
            Id = 101,
            PipelineId = 1,
            CommitHash = DefaultCommit,
            ParametersJson = "{}",
            AdditionalVariablesJson = JsonSerializer.Serialize(
                new Dictionary<string, string> { [PipelineRunService.ResumeSourceRunVariable] = sourceParentRunId.ToString() }),
            Pipeline = sourceParent.Pipeline
        };
        var manifestHash = new string('a', 64);
        var server = new Server
        {
            Id = 77,
            AgentVersion = "1.2.3",
            ScannerCapabilitiesJson = JsonSerializer.Serialize(
                new[] { $"scanner-manifest:sha256:{manifestHash}" })
        };
        var task = new ServerTask
        {
            Id = 300,
            ServerId = server.Id,
            Operation = scannerTask ? OperationKind.PipelineRunScanner : OperationKind.None,
            AssignedAgentVersion = server.AgentVersion,
            AssignedScannerManifestSha256 = scannerTask ? manifestHash : null
        };
        var checkpoint = new PipelineRun
        {
            Id = checkpointRunId,
            PipelineId = target.Id,
            Status = PipelineStatus.Success,
            CommitHash = DefaultCommit,
            ParametersJson = "{}",
            YamlSnapshot = preparation.YamlSnapshot,
            ResolvedVariablesJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["AETHEUS_VERSION"] = typeof(PipelineVariableResolver).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"
            }),
            Pipeline = target,
            StepRuns = [new PipelineStepRun { Id = 301, Task = task }]
        };
        var bytes = "verified checkpoint"u8.ToArray();
        var artifact = new PipelineArtifact
        {
            Id = 400,
            PipelineRunId = checkpointRunId,
            PipelineId = target.Id,
            FilePath = "7/2/200/checkpoint.zip",
            SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
        };
        var triggerStep = new PipelineStepRun
        {
            Id = 500,
            PipelineRunId = currentParent.Id,
            StageName = "CI",
            StepName = "TriggerCI",
            Status = TaskExecutionStatus.Pending
        };

        _repoMock.GetRunDetailAsync(sourceParentRunId, Arg.Any<CancellationToken>()).Returns(sourceParent);
        _repoMock.FindTriggeredRunIdByPipelineNameAsync(
            sourceParentRunId, targetName, Arg.Any<CancellationToken>()).Returns(checkpointRunId);
        _repoMock.GetRunDetailAsync(checkpointRunId, Arg.Any<CancellationToken>()).Returns(checkpoint);
        _repoMock.FindServerByIdAsync(server.Id, Arg.Any<CancellationToken>()).Returns(server);
        _artifactRepoMock.GetByRunAsync(checkpointRunId, Arg.Any<CancellationToken>()).Returns([artifact]);
        _artifactStorageMock.OpenArtifact(artifact.FilePath).Returns(_ => new MemoryStream(bytes));

        return new CheckpointReuseFixture(
            currentParent, target, preparation, checkpoint, triggerStep, artifact, server,
            new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc));
    }

    private sealed record CheckpointReuseFixture(
        PipelineRun CurrentParent,
        Pipeline Target,
        PipelineRunPreparation Preparation,
        PipelineRun Checkpoint,
        PipelineStepRun TriggerStep,
        PipelineArtifact Artifact,
        Server Server,
        DateTime Now);

    // --- Static helpers via reflection ---

    private static List<string> InvokeFindReadyStages(
        List<PipelineStepRun> pendingSteps, PipelineYamlDefinition definition,
        List<string> completedStages, List<string>? terminalStages = null)
    {
        var method = typeof(PipelineStageDispatchPlanner).GetMethod("FindReadyStages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (List<string>)method.Invoke(null, [pendingSteps, definition, completedStages, terminalStages ?? completedStages])!;
    }

    [Fact]
    public void PropagateLocalDeploymentContext_LocalParentPinsNestedTriggerToSameAgent()
    {
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PipelineDeploymentTargetGuard.TargetVariable] = PipelineDeploymentTargetGuard.Local,
            [PipelineDeploymentTargetGuard.LocalAgentVariable] = " release-vpssim "
        };
        var child = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PipelineDeploymentTargetGuard.TargetVariable] = "production",
            [PipelineDeploymentTargetGuard.LocalAgentVariable] = "web-01"
        };

        InvokePropagateLocalDeploymentContext(parent, child);

        Assert.Equal(PipelineDeploymentTargetGuard.Local, child[PipelineDeploymentTargetGuard.TargetVariable]);
        Assert.Equal("release-vpssim", child[PipelineDeploymentTargetGuard.LocalAgentVariable]);
    }

    [Fact]
    public void ApplyUpstreamContext_OverridesWhatTheStepDeclaredAndNamesTheParentRun()
    {
        // Chained children receive the actual initiating trigger metadata.
        var childVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BUILD_TRIGGEREDBY"] = "webhook",
            ["UPSTREAM_PIPELINE"] = "forged",
            [PipelineScannerTaskFactory.UpstreamTemplateVariable] = "application-candidate"
        };

        PipelineTriggerStepCoordinator.ApplyUpstreamContext(childVariables, "aetheus-candidate", 2328, [3, 5]);

        // A definition cannot claim a candidate parent through the template either.
        Assert.Equal(string.Empty, childVariables[PipelineScannerTaskFactory.UpstreamTemplateVariable]);
        Assert.Equal("trigger:aetheus-candidate#2328", childVariables["BUILD_TRIGGEREDBY"]);
        Assert.Equal("aetheus-candidate", childVariables["UPSTREAM_PIPELINE"]);
        Assert.Equal("2328", childVariables["UPSTREAM_RUN_ID"]);
        Assert.Equal("3,5", childVariables["UPSTREAM_CHAIN"]);
    }

    [Fact]
    public void ApplyTriggerSourceContext_IndependentPinnedCommitSubstitutesExactParentOutput()
    {
        const string candidateCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var step = new PipelineStepDefinition
        {
            Type = "trigger",
            InheritSource = false,
            SourceBranch = "develop",
            SourceCommit = "$(CANDIDATE_COMMIT)"
        };
        var parent = new PipelineRun
        {
            CommitHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            BranchName = "main"
        };
        var parentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CANDIDATE_COMMIT"] = candidateCommit
        };
        var childVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_SOURCE_COMMIT"] = parent.CommitHash
        };

        var error = PipelineTriggerStepCoordinator.ApplyTriggerSourceContext(step, parent, parentVariables, childVariables);

        Assert.Null(error);
        Assert.Equal("develop", childVariables["AETHEUS_RUN_BRANCH"]);
        Assert.Equal(candidateCommit, childVariables["AETHEUS_SOURCE_COMMIT"]);
    }

    [Fact]
    public void ApplyTriggerSourceContext_InvalidPinnedCommitFailsClosed()
    {
        var step = new PipelineStepDefinition
        {
            Type = "trigger",
            InheritSource = false,
            SourceBranch = "develop",
            SourceCommit = "$(CANDIDATE_COMMIT)"
        };
        var parentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CANDIDATE_COMMIT"] = "not-a-commit"
        };
        var childVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var error = PipelineTriggerStepCoordinator.ApplyTriggerSourceContext(step, new PipelineRun(), parentVariables, childVariables);

        Assert.Equal("source_commit must resolve to a full hexadecimal Git commit hash.", error);
        Assert.False(childVariables.ContainsKey("AETHEUS_SOURCE_COMMIT"));
    }

    [Fact]
    public void PropagateLocalDeploymentContext_NonLocalParentLeavesChildRoutingUntouched()
    {
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PipelineDeploymentTargetGuard.TargetVariable] = "production",
            [PipelineDeploymentTargetGuard.LocalAgentVariable] = "release-vpssim"
        };
        var child = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PipelineDeploymentTargetGuard.TargetVariable] = "production"
        };

        InvokePropagateLocalDeploymentContext(parent, child);

        Assert.Equal("production", child[PipelineDeploymentTargetGuard.TargetVariable]);
        Assert.False(child.ContainsKey(PipelineDeploymentTargetGuard.LocalAgentVariable));
    }

    private static void InvokePropagateLocalDeploymentContext(
        IReadOnlyDictionary<string, string> parent,
        IDictionary<string, string> child)
    {
        var method = typeof(PipelineTriggerStepCoordinator).GetMethod(
            "PropagateLocalDeploymentContext",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        method.Invoke(null, [parent, child]);
    }

    // MarkStepsAs now belongs to PipelineRunFinalizer and is part of its public contract, so the test
    // calls it directly instead of reaching into a private through reflection.
    private void InvokeMarkStepsAs(List<PipelineStepRun> steps, TaskExecutionStatus status)
        => new PipelineRunFinalizer(
                _repoMock, _hubMock, Substitute.For<IDomainEventDispatcher>(),
                _secretMaskingMock, TimeProvider.System)
            .MarkStepsAs(steps, status);

    private static Dictionary<string, string> InvokeResolveLegVariables(Dictionary<string, string> stageVars, string legKey, List<Dictionary<string, string>> matrixLegs)
        => PipelineRunHelpers.ResolveLegVariables(stageVars, legKey, matrixLegs);

    private static PipelineStatus InvokeDecideFinalStatus(
        bool cancelled, bool blockingFailure, bool swallowedFailure, bool rolledBack = false, bool rejected = false)
        => PipelineRunScheduler.DecideFinalStatus(cancelled, blockingFailure, swallowedFailure, rolledBack, rejected);

    private static bool InvokeHasNonContinuableFailures(List<PipelineStepRun> failedSteps)
    {
        var method = typeof(PipelineRunScheduler).GetMethod("HasNonContinuableFailures", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (bool)method.Invoke(null, [failedSteps])!;
    }

    // Every condition these tests read is a supported form: the verdict must carry no error.
    private static bool InvokeEvaluateCondition(string? condition, Dictionary<string, string> variables, List<string> completedStages, bool previousStageFailed)
    {
        Assert.Empty(completedStages);
        var verdict = PipelineRunHelpers.EvaluateCondition(condition, variables, previousStageFailed);
        Assert.Null(verdict.Error);
        return verdict.Met;
    }

    private static bool InvokeEvaluateVariableCondition(string condition, Dictionary<string, string> variables)
        => InvokeEvaluateCondition(condition, variables, [], previousStageFailed: false);

    private static Dictionary<string, string> InvokeParseOutputVariables(string? output)
        => PipelineRunHelpers.ParseOutputVariablesFromLogs(output);

    private static List<Dictionary<string, string>> InvokeExpandMatrix(Dictionary<string, List<string>>? matrix)
        => PipelineRunHelpers.ExpandMatrix(matrix);

    // --- EvaluateCondition ---

    [Fact]
    public void EvaluateCondition_NullOrEmpty_ReturnsTrue()
    {
        Assert.True(InvokeEvaluateCondition(null, [], [], false));
        Assert.True(InvokeEvaluateCondition("", [], [], false));
        Assert.True(InvokeEvaluateCondition("  ", [], [], false));
    }

    [Fact]
    public void EvaluateCondition_Always_ReturnsTrue()
    {
        Assert.True(InvokeEvaluateCondition("always()", [], [], true));
    }

    [Fact]
    public void EvaluateCondition_Succeeded_ReturnsTrueWhenNoFailure()
    {
        Assert.True(InvokeEvaluateCondition("succeeded()", [], [], false));
        Assert.False(InvokeEvaluateCondition("succeeded()", [], [], true));
    }

    [Fact]
    public void EvaluateCondition_Failed_ReturnsTrueWhenPreviousFailed()
    {
        Assert.True(InvokeEvaluateCondition("failed()", [], [], true));
        Assert.False(InvokeEvaluateCondition("failed()", [], [], false));
    }

    [Fact]
    public void EvaluateCondition_Cancelled_AlwaysReturnsFalse()
    {
        Assert.False(InvokeEvaluateCondition("cancelled()", [], [], false));
        Assert.False(InvokeEvaluateCondition("cancelled()", [], [], true));
    }

    [Fact]
    public void EvaluateCondition_VariableCondition_DelegatesToEvaluateVariableCondition()
    {
        var vars = new Dictionary<string, string> { ["env"] = "prod" };
        Assert.True(InvokeEvaluateCondition("eq(variables['env'], 'prod')", vars, [], false));
        Assert.False(InvokeEvaluateCondition("eq(variables['env'], 'dev')", vars, [], false));
    }

    [Fact]
    public void ConditionEvidence_ReportsActualValuesWithoutLeakingSecrets()
    {
        var values = PipelineConditionEvidence.CaptureVariables(
            "eq(variables['MODE'], 'prod')",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MODE"] = "demo",
                ["TOKEN"] = "sensitive"
            },
            new HashSet<string>(["TOKEN"], StringComparer.OrdinalIgnoreCase));

        var secretValues = PipelineConditionEvidence.CaptureVariables(
            "eq(variables['TOKEN'], 'expected')",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TOKEN"] = "sensitive"
            },
            new HashSet<string>(["TOKEN"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal("demo", values["MODE"]);
        Assert.Equal("[masked]", secretValues["TOKEN"]);
    }

    [Fact]
    public void ConditionEvidence_CompactsAtPersistenceLimitsWithStableHashesAndValidJson()
    {
        var exactCondition = new string('a', PipelineConditionEvidence.MaxConditionLength);
        Assert.Equal(exactCondition, PipelineConditionEvidence.CompactCondition(exactCondition));

        var oversizedCondition = exactCondition + "tail";
        var compactedCondition = PipelineConditionEvidence.CompactCondition(oversizedCondition);
        Assert.Equal(PipelineConditionEvidence.MaxConditionLength, compactedCondition.Length);
        Assert.Contains("sha256:", compactedCondition, StringComparison.Ordinal);
        Assert.Equal(compactedCondition, PipelineConditionEvidence.CompactCondition(oversizedCondition));

        var variables = Enumerable.Range(0, 40).ToDictionary(
            index => $"VAR_{index:D2}",
            index => new string((char)('a' + index % 26), 600));
        var json = PipelineConditionEvidence.SerializeVariables(variables);

        Assert.NotNull(json);
        Assert.True(json.Length <= PipelineConditionEvidence.MaxVariablesJsonLength);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        Assert.NotNull(parsed);
        Assert.Equal("true", parsed["aetheus:evidence:truncated"]);
        Assert.Equal(64, parsed["aetheus:evidence:sha256"].Length);
        Assert.Equal(json, PipelineConditionEvidence.SerializeVariables(variables));
    }

    // --- EvaluateVariableCondition ---

    [Fact]
    public void EvaluateVariableCondition_EqMatch_ReturnsTrue()
    {
        var vars = new Dictionary<string, string> { ["region"] = "us-east" };
        Assert.True(InvokeEvaluateVariableCondition("eq(variables['region'], 'us-east')", vars));
    }

    [Fact]
    public void EvaluateVariableCondition_EqMismatch_ReturnsFalse()
    {
        var vars = new Dictionary<string, string> { ["region"] = "eu-west" };
        Assert.False(InvokeEvaluateVariableCondition("eq(variables['region'], 'us-east')", vars));
    }

    [Fact]
    public void EvaluateVariableCondition_EqMissingVar_ReturnsFalse()
    {
        Assert.False(InvokeEvaluateVariableCondition("eq(variables['missing'], 'val')", new Dictionary<string, string>()));
    }

    [Fact]
    public void EvaluateVariableCondition_NeMatch_ReturnsTrue()
    {
        var vars = new Dictionary<string, string> { ["env"] = "dev" };
        Assert.True(InvokeEvaluateVariableCondition("ne(variables['env'], 'prod')", vars));
    }

    [Fact]
    public void EvaluateVariableCondition_NeMissingVar_ReturnsTrue()
    {
        Assert.True(InvokeEvaluateVariableCondition("ne(variables['missing'], 'val')", new Dictionary<string, string>()));
    }

    [Fact]
    public void EvaluateVariableCondition_UnknownFormat_IsInvalidAndNeverMet()
    {
        // Fail-closed: unknown condition syntax never permits the stage to run, and comes back as an
        // error the engine fails the stage with, not as a plain "not met".
        var verdict = PipelineRunHelpers.EvaluateCondition(
            "something_unknown()", new Dictionary<string, string>(), previousStageFailed: false);
        Assert.False(verdict.Met);
        Assert.NotNull(verdict.Error);
    }

    // --- ParseOutputVariables ---

    [Fact]
    public void ParseOutputVariables_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(InvokeParseOutputVariables(null));
        Assert.Empty(InvokeParseOutputVariables(""));
    }

    [Fact]
    public void ParseOutputVariables_SingleVariable_Parsed()
    {
        var output = "some log\n##aetheus[setvariable name=VERSION]1.2.3\nmore log";
        var result = InvokeParseOutputVariables(output);
        Assert.Single(result);
        Assert.Equal("1.2.3", result["VERSION"]);
    }

    [Fact]
    public void ParseOutputVariables_MultipleVariables_AllParsed()
    {
        var output = "##aetheus[setvariable name=A]valA\n##aetheus[setvariable name=B]valB";
        var result = InvokeParseOutputVariables(output);
        Assert.Equal(2, result.Count);
        Assert.Equal("valA", result["A"]);
        Assert.Equal("valB", result["B"]);
    }

    [Fact]
    public void ParseOutputVariables_NoMatch_ReturnsEmpty()
    {
        var result = InvokeParseOutputVariables("just normal output\nno special lines");
        Assert.Empty(result);
    }

    // --- ExpandMatrix ---

    [Fact]
    public void ExpandMatrix_NullOrEmpty_ReturnsSingleEmptyLeg()
    {
        var result = InvokeExpandMatrix(null);
        Assert.Single(result);
        Assert.Empty(result[0]);

        result = InvokeExpandMatrix(new Dictionary<string, List<string>>());
        Assert.Single(result);
        Assert.Empty(result[0]);
    }

    [Fact]
    public void ExpandMatrix_SingleDimension_ReturnsOneLegPerValue()
    {
        var matrix = new Dictionary<string, List<string>> { ["os"] = ["ubuntu", "windows"] };
        var result = InvokeExpandMatrix(matrix);
        Assert.Equal(2, result.Count);
        Assert.Equal("ubuntu", result[0]["os"]);
        Assert.Equal("windows", result[1]["os"]);
    }

    [Fact]
    public void ExpandMatrix_TwoDimensions_ReturnsCartesianProduct()
    {
        var matrix = new Dictionary<string, List<string>>
        {
            ["os"] = ["ubuntu", "windows"],
            ["arch"] = ["x64", "arm64"]
        };
        var result = InvokeExpandMatrix(matrix);
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void ExpandMatrix_ThreeDimensions_ReturnsFullProduct()
    {
        var matrix = new Dictionary<string, List<string>>
        {
            ["os"] = ["a", "b"],
            ["arch"] = ["x", "y"],
            ["runtime"] = ["1", "2"]
        };
        var result = InvokeExpandMatrix(matrix);
        Assert.Equal(8, result.Count);
    }

    [Fact]
    public void ExpandMatrix_ProductBeyondLimit_IsRejectedBeforeMaterialization()
    {
        var matrix = new Dictionary<string, List<string>>
        {
            ["a"] = Enumerable.Range(1, 17).Select(i => $"a{i}").ToList(),
            ["b"] = Enumerable.Range(1, 17).Select(i => $"b{i}").ToList()
        };

        var error = Assert.Throws<ArgumentException>(() => InvokeExpandMatrix(matrix));

        Assert.Contains(PipelineRunHelpers.MaxMatrixLegs.ToString(), error.Message, StringComparison.Ordinal);
    }

    // --- S2 (audit 2026-07-06): ValidateMatrixValues (matrix values are shell-spliced) ---

    private static List<PipelineStageDefinition> StageWithMatrix(Dictionary<string, List<string>>? matrix) =>
        [new PipelineStageDefinition { Name = "build", Matrix = matrix }];

    [Theory]
    [InlineData("ubuntu-22.04")]
    [InlineData("net8.0")]
    [InlineData("linux/amd64")]
    [InlineData("node:20")]
    [InlineData("mcr.microsoft.com/dotnet/sdk:8.0")]
    [InlineData("key=value")]   // F-ENG-03: '=' now allowed (shell-inert in a bare word)
    [InlineData("50%")]         // F-ENG-03: '%' now allowed
    [InlineData("1.0,2.0")]     // F-ENG-03: ',' now allowed
    public void ValidateMatrixValues_LegitimateShapes_ReturnNoError(string value)
    {
        var errors = PipelineRunHelpers.ValidateMatrixValues(
            StageWithMatrix(new Dictionary<string, List<string>> { ["axis"] = [value] }));

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateMatrixValues_LongQualifiedDigest_ReturnsNoError()
    {
        var value = $"registry.example.test:5000/{new string('a', 900)}@sha256:{new string('b', 64)}";

        var errors = PipelineRunHelpers.ValidateMatrixValues(
            StageWithMatrix(new Dictionary<string, List<string>> { ["image"] = [value] }));

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("bad-axis")]
    [InlineData("9axis")]
    [InlineData("axis.name")]
    [InlineData("axis name")]
    public void ValidateMatrixValues_UnsafeAxisName_IsRejected(string axis)
    {
        var errors = PipelineRunHelpers.ValidateMatrixValues(
            StageWithMatrix(new Dictionary<string, List<string>> { [axis] = ["value"] }));

        Assert.Contains(errors, error => error.Contains("safe variable name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("$(rm -rf /)")]           // command substitution
    [InlineData("a;whoami")]              // command separator
    [InlineData("a\nwhoami")]             // newline breaks out of the quoted context
    [InlineData("net8.0\n")]              // trailing newline (the ^...$ vs \A...\z bypass)
    [InlineData("-oProxyCommand=evil")]   // leading dash = argument injection
    [InlineData("`id`")]                  // backtick substitution
    [InlineData("Release --output /etc")] // space = shell word-splitting into extra arguments
    [InlineData("C:\\Windows\\System32")] // backslash = shell escape, still rejected (F-ENG-03)
    [InlineData("")]                      // empty value
    public void ValidateMatrixValues_ShellShapedValues_AreRejected(string value)
    {
        var errors = PipelineRunHelpers.ValidateMatrixValues(
            StageWithMatrix(new Dictionary<string, List<string>> { ["axis"] = [value] }));

        var error = Assert.Single(errors);
        Assert.Contains("axis", error);
        Assert.Contains("build", error);
    }

    [Fact]
    public void ValidateMatrixValues_NoMatrix_ReturnsNoError()
    {
        Assert.Empty(PipelineRunHelpers.ValidateMatrixValues(StageWithMatrix(null)));
        Assert.Empty(PipelineRunHelpers.ValidateMatrixValues(
            StageWithMatrix(new Dictionary<string, List<string>>())));
    }

    [Fact]
    public void ValidateMatrixValues_ReportsEveryOffendingValue()
    {
        var errors = PipelineRunHelpers.ValidateMatrixValues(
            StageWithMatrix(new Dictionary<string, List<string>>
            {
                ["os"] = ["ubuntu", "a;b"],
                ["arch"] = ["`x`"]
            }));

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void ValidateMatrixValues_EnforcesAxesValuesAndProductLimits()
    {
        var tooManyAxes = Enumerable.Range(1, PipelineRunHelpers.MaxMatrixAxes + 1)
            .ToDictionary(i => $"axis{i}", _ => new List<string> { "one" });
        var tooManyValues = Enumerable.Range(1, PipelineRunHelpers.MaxMatrixValuesPerAxis + 1)
            .Select(i => $"v{i}").ToList();
        var excessiveProduct = new Dictionary<string, List<string>>
        {
            ["a"] = Enumerable.Range(1, 17).Select(i => $"a{i}").ToList(),
            ["b"] = Enumerable.Range(1, 17).Select(i => $"b{i}").ToList()
        };

        Assert.Contains(PipelineRunHelpers.ValidateMatrixValues(StageWithMatrix(tooManyAxes)),
            e => e.Contains("axes", StringComparison.Ordinal));
        Assert.Contains(PipelineRunHelpers.ValidateMatrixValues(
                StageWithMatrix(new Dictionary<string, List<string>> { ["axis"] = tooManyValues })),
            e => e.Contains("values", StringComparison.Ordinal));
        Assert.Contains(PipelineRunHelpers.ValidateMatrixValues(StageWithMatrix(excessiveProduct)),
            e => e.Contains("legs", StringComparison.Ordinal));
    }

    // --- F-EXEC-1: ResolveCandidateTargetServerIdsAsync (authorization target set) ---

    [Fact]
    public async Task ResolveCandidateTargetServerIds_AgentSelector_ReturnsAgentMatchedIds()
    {
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = yaml, Runs = [] });
        _repoMock.FindCandidateTargetServerIdsAsync(
                null, null, "linux-01", OsType.Unknown, Arg.Is<int?>(organization => organization == null), false, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([7, 9]);

        var ids = await _sut.ResolveCandidateTargetServerIdsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 7, 9 }, ids.OrderBy(i => i).ToArray());
        await _repoMock.Received(1).FindCandidateTargetServerIdsAsync(
            null, null, "linux-01", OsType.Unknown, Arg.Is<int?>(organization => organization == null), false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveCandidateTargetServerIds_PoolTakesPrecedenceOverAgent()
    {
        // Mirrors ResolveServerAsync precedence: pool > environment > agent. When a stage sets
        // both, only the pool enumerator must be consulted (the agent set must NOT widen/leak).
        var yaml = """
            name: deploy
            trigger: manual
            stages:
              - name: build
                pool: prod
                agent: linux-01
                steps:
                  - name: compile
                    shell: dotnet build
            """;
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Deploy", YamlDefinition = yaml, Runs = [] });
        _repoMock.FindCandidateTargetServerIdsAsync(
                "prod", null, "linux-01", OsType.Unknown, Arg.Is<int?>(organization => organization == null), false, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([3]);

        var ids = await _sut.ResolveCandidateTargetServerIdsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 3 }, ids.ToArray());
        await _repoMock.Received(1).FindCandidateTargetServerIdsAsync(
            "prod", null, "linux-01", OsType.Unknown, Arg.Is<int?>(organization => organization == null), false, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveCandidateTargetServerIds_InvalidYaml_IsRejected()
    {
        _repoMock.FindPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Pipeline { Id = 1, Name = "Bad", YamlDefinition = "{{invalid", Runs = [] });

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ResolveCandidateTargetServerIdsAsync(1, ct: TestContext.Current.CancellationToken));
    }

    private void ArrangeAdvanceToStage(string yaml, string stageName, params PipelineStepRun[] steps)
        => ArrangeAdvanceToStageWithVariables(yaml, stageName, "{}", steps);

    private void ArrangeAdvanceToStageWithVariables(
        string yaml, string stageName, string resolvedVariablesJson, params PipelineStepRun[] steps)
        => ArrangeAdvanceToStageWithVariables(yaml, stageName, resolvedVariablesJson, PipelineStatus.Pending, steps);

    private void ArrangeAdvanceToStageWithVariables(
        string yaml, string stageName, string resolvedVariablesJson, PipelineStatus runStatus,
        params PipelineStepRun[] steps)
    {
        var run = new PipelineRun
        {
            Id = 1,
            PipelineId = 1,
            Status = runStatus,
            AdditionalVariablesJson = resolvedVariablesJson,
            ResolvedVariablesJson = resolvedVariablesJson,
            CommitHash = DefaultCommit,
            Pipeline = new Pipeline
            {
                Id = 1,
                ProjectId = 7,
                SourceRepositoryId = 11,
                YamlDefinition = yaml
            }
        };
        var server = new Server
        {
            Id = 10,
            Name = "linux-01",
            OsType = OsType.Linux,
            DeploymentTargetAvailable = true
        };

        _repoMock.AreAllStepsInStageCompletedAsync(1, "build", Arg.Any<CancellationToken>()).Returns(true);
        _repoMock.GetFailedStepRunsInStageAsync(1, "build", Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>()).Returns(run);
        _repoMock.GetPendingStepRunsAsync(1, Arg.Any<CancellationToken>()).Returns(steps.ToList());
        _repoMock.GetCompletedStageNamesAsync(1, Arg.Any<CancellationToken>())
            .Returns([PipelineRunHelpers.SystemPrepareStage, "build"]);
        _repoMock.GetActiveStageNamesAsync(1, Arg.Any<CancellationToken>()).Returns([]);
        _repoMock.FindOnlineServerByAgentAsync(Arg.Any<string>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(server);
        _repoMock.FindAnyOnlineRunnerAsync(Arg.Any<int?>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(server);
        _repoMock.FindOnlineDeployTargetAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(server);
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private void ArrangeEnvironment(params EnvironmentCheck[] checks)
    {
        _repoMock.FindEnvironmentByNameAsync("prod", Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new Aetheus.Back.Data.Entities.Environment { Id = 6, Name = "prod", RequireApproval = false });
        _repoMock.GetEnvironmentChecksAsync(6, Arg.Any<CancellationToken>()).Returns(checks.ToList());
        _repoMock.FindOnlineServerInEnvironmentAsync("prod", Arg.Any<int?>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 10, Name = "prod-01", OsType = OsType.Linux });
    }

    private static string EnvironmentStageYaml() => """
        name: release
        trigger: manual
        stages:
          - name: build
            agent: linux-01
            steps:
              - name: compile
                shell: dotnet build
          - name: ship
            environment: prod
            steps:
              - name: publish
                shell: echo publish
        """;

    private sealed class StaticResponseHandler(System.Net.HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
