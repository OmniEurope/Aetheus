// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Services;
using Aetheus.Agent.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Agent.Core.Tests;

public class HeartbeatServiceTests
{
    private readonly IServerApiClient _apiClientMock = Substitute.For<IServerApiClient>();
    private readonly IMetricsCollector _metricsCollectorMock = Substitute.For<IMetricsCollector>();
    private readonly IServiceCollector _serviceCollectorMock = Substitute.For<IServiceCollector>();
    private readonly IDockerCollector _dockerCollectorMock = Substitute.For<IDockerCollector>();
    private readonly IApacheCollector _apacheCollectorMock = Substitute.For<IApacheCollector>();
    private readonly ICertbotCollector _certbotCollectorMock = Substitute.For<ICertbotCollector>();
    private readonly IMailCollector _mailCollectorMock = Substitute.For<IMailCollector>();
    private readonly ITeamspeakCollector _teamspeakCollectorMock = Substitute.For<ITeamspeakCollector>();
    private readonly IPortsentryCollector _portsentryCollectorMock = Substitute.For<IPortsentryCollector>();
    private readonly IRkhunterCollector _rkhunterCollectorMock = Substitute.For<IRkhunterCollector>();
    private readonly ISecurityUpdatesCollector _securityUpdatesCollectorMock = Substitute.For<ISecurityUpdatesCollector>();
    private readonly IFirewallCollector _firewallCollectorMock = Substitute.For<IFirewallCollector>();
    private readonly IListeningPortsCollector _listeningPortsCollectorMock = Substitute.For<IListeningPortsCollector>();
    private readonly ISudoersHashCollector _sudoersHashCollectorMock = Substitute.For<ISudoersHashCollector>();
    private readonly Aetheus.Agent.Core.Collectors.Ssh.ISshConfigCollector _sshConfigCollectorMock =
        Substitute.For<Aetheus.Agent.Core.Collectors.Ssh.ISshConfigCollector>();
    private readonly IDockerStorageMaintenance _dockerStorageMaintenanceMock = Substitute.For<IDockerStorageMaintenance>();
    private readonly IShellRunner _shellRunnerMock = Substitute.For<IShellRunner>();
    private readonly AgentState _agentState;
    private readonly EnrollmentService _enrollment;
    private readonly IOptions<AetheusAgentOptions> _options;

    public HeartbeatServiceTests()
    {
        _agentState = new AgentState
        {
            ServerId = 1,
            BearerToken = "test-token"
        };

        _options = Options.Create(new AetheusAgentOptions
        {
            ServerUrl = "http://localhost:5301",
            HeartbeatIntervalSeconds = 1,
            HeartbeatCollectionTimeoutSeconds = 1
        });

        _enrollment = CreateEnrolledEnrollmentService();

        _metricsCollectorMock.Collect().Returns(new ServerHeartbeatDto
        {
            CpuPercent = 25.0,
            MemoryUsedMb = 2048,
            MemoryTotalMb = 8192,
            Disks = [],
            Services = []
        });

        _serviceCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ServiceInfoDto>());

        _dockerCollectorMock.CollectAllAsync(Arg.Any<CancellationToken>())
            .Returns(new DockerDataDto());

        _apacheCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new ApacheDataDto());

        _certbotCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new CertbotDataDto());

        _mailCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new MailDataDto());

        _teamspeakCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new TeamspeakDataDto());

        _portsentryCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new PortsentryDataDto());

        _rkhunterCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new RkhunterDataDto());

        _securityUpdatesCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new SecurityUpdatesDataDto());

        _firewallCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new FirewallDataDto());

        _listeningPortsCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ObservedPortDto>());

        _sudoersHashCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());

        _sshConfigCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new SshConfigDataDto
            {
                Collected = true,
                PermitRootLogin = new SshSettingDto { Value = "no", Source = "/etc/ssh/sshd_config:3" }
            });

        _dockerStorageMaintenanceMock.CollectDiagnosticsAsync(Arg.Any<CancellationToken>())
            .Returns(new StorageDiagnosticsDto { BuildCacheBytes = 1234 });
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_SendsHeartbeatWhenEnrolled()
    {
        var service = CreateService();

        // E-3: drive one iteration directly - deterministic, no PeriodicTimer / sleep.
        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.Received(1).SendHeartbeatAsync(1, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_StampsAgentVersionAndCollectedMetrics()
    {
        var service = CreateService();

        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        // The iteration must merge the collected metrics and stamp the agent version
        // before posting - guards the heartbeat payload, not just that a call happened.
        await _apiClientMock.Received(1).SendHeartbeatAsync(1, Arg.Is<ServerHeartbeatDto>(h =>
                h.CpuPercent == 25.0
                && h.MemoryUsedMb == 2048
                && h.MemoryTotalMb == 8192
                && h.SudoersInventoryAvailable == true
                && h.SshConfig != null && h.SshConfig.Collected && h.SshConfig.PermitRootLogin.Value == "no"
                && !string.IsNullOrEmpty(h.AgentVersion)
                && h.AgentProtocolVersion == AgentProtocol.CurrentVersion
                && h.AgentCapabilities != null
                && h.AgentCapabilities.Contains(AgentCapabilities.SelfUpdate)
                && h.AgentCapabilities.SequenceEqual(h.AgentCapabilities.Order(StringComparer.Ordinal))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_WithPipelineRunner_PublishesPipelineFunctions()
    {
        _shellRunnerMock.RunExecAsync(
                "git",
                Arg.Is<IReadOnlyList<string>>(args => args.SequenceEqual(new[] { "--version" })),
                Arg.Any<CancellationToken>(),
                Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, "git version 2.50.0", string.Empty));
        var service = CreateService();

        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        // Build functions work on a checked-out workspace, so they follow pipeline.build; the
        // deployment variant needs the deploy grant, absent here.
        await _apiClientMock.Received(1).SendHeartbeatAsync(1, Arg.Is<ServerHeartbeatDto>(h =>
                h.PipelineRunnerAvailable == true
                && h.AgentCapabilities!.Contains(AgentCapabilities.PipelineBuild)
                && h.AgentCapabilities.Contains(AgentCapabilities.PipelineFunctions)
                && !h.AgentCapabilities.Contains(AgentCapabilities.DeploymentFunctions)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_WithoutPipelineRunner_WithdrawsPipelineFunctions()
    {
        var service = CreateService();

        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.Received(1).SendHeartbeatAsync(1, Arg.Is<ServerHeartbeatDto>(h =>
                h.AgentCapabilities != null
                && !h.AgentCapabilities.Contains(AgentCapabilities.PipelineBuild)
                && !h.AgentCapabilities.Contains(AgentCapabilities.PipelineFunctions)
                && !h.AgentCapabilities.Contains(AgentCapabilities.DeploymentFunctions)),
            Arg.Any<CancellationToken>());
    }

    // R-119: the server sections' new typed operations need the agent version (software capability) AND the grant
    // their helper runs under; without the grant the agent never claims them, so the task explains itself instead of
    // failing on a sudo refusal.
    [Fact]
    public async Task CollectAndSendHeartbeatAsync_WithoutTheGrants_DoesNotClaimTheSectionOperations()
    {
        var service = CreateService();

        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(AgentCapabilities.ApacheModules, LastSentHeartbeat().AgentCapabilities!);
        Assert.DoesNotContain(AgentCapabilities.RkhunterSetup, LastSentHeartbeat().AgentCapabilities!);
        Assert.DoesNotContain(AgentCapabilities.TeamspeakServiceControl, LastSentHeartbeat().AgentCapabilities!);
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_WithTheGrants_ClaimsTheSectionOperations()
    {
        _sudoersHashCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>
            {
                ["aetheus-apache"] = "a",
                ["aetheus-package"] = "p",
                ["aetheus-rkhunter"] = "r",
                ["aetheus-teamspeak"] = "t"
            });
        var service = CreateService();

        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        Assert.Contains(AgentCapabilities.ApacheModules, LastSentHeartbeat().AgentCapabilities!);
        Assert.Contains(AgentCapabilities.RkhunterSetup, LastSentHeartbeat().AgentCapabilities!);
        Assert.Contains(AgentCapabilities.TeamspeakServiceControl, LastSentHeartbeat().AgentCapabilities!);
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_RkhunterSetup_NeedsBothThePackageAndTheRkhunterGrants()
    {
        _sudoersHashCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["aetheus-rkhunter"] = "r" });
        var service = CreateService();

        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(AgentCapabilities.RkhunterSetup, LastSentHeartbeat().AgentCapabilities!);
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_ApiFailure_DoesNotCorruptNextIteration()
    {
        _apiClientMock.SendHeartbeatAsync(1, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new HttpRequestException("Connection refused"),
                _ => (ServerHeartbeatResponseDto?)null);

        var service = CreateService();

        // First iteration throws (the loop swallows it and retries next tick); the SECOND
        // iteration must still post a heartbeat. Asserting two real iterations replaces the
        // old "sleep 4s and hope ≥2 calls landed".
        await Assert.ThrowsAsync<HttpRequestException>(() => service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken));
        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.Received(2).SendHeartbeatAsync(1, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_ApiFailure_DoesNotRefreshWatchdogSuccess()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero));
        var health = new AgentRuntimeHealth(time);
        health.MarkHeartbeatSuccess();
        var previousSuccess = health.LastHeartbeatSuccessAt;
        time.Advance(TimeSpan.FromMinutes(1));
        _apiClientMock.SendHeartbeatAsync(1, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        var service = CreateService(time, health);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken));

        Assert.Equal(previousSuccess, health.LastHeartbeatSuccessAt);
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_ApiSuccess_RefreshesWatchdogSuccess()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero));
        var health = new AgentRuntimeHealth(time);
        health.MarkHeartbeatSuccess();
        time.Advance(TimeSpan.FromMinutes(1));
        var service = CreateService(time, health);

        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        Assert.Equal(time.GetUtcNow(), health.LastHeartbeatSuccessAt);
    }

    [Fact]
    public async Task ExecuteAsync_Enrolled_SendsInitialHeartbeatBeforeFirstTimerTick()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 5, 8, 0, 0, TimeSpan.Zero));
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _apiClientMock.SendHeartbeatAsync(1, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                sent.TrySetResult();
                return (ServerHeartbeatResponseDto?)null;
            });
        var service = CreateService(time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.LoopStarted.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.Received(1).SendHeartbeatAsync(
            1,
            Arg.Is<ServerHeartbeatDto>(heartbeat =>
                heartbeat.AgentProtocolVersion == AgentProtocol.CurrentVersion
                && heartbeat.AgentCapabilities != null
                && heartbeat.AgentCapabilities.Contains(AgentCapabilities.SelfUpdate)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ARequestedHeartbeat_IsSentWithoutWaitingForTheTick()
    {
        // The clock never advances, so only the request can explain the second beat.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var health = new AgentRuntimeHealth(time);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beats = 0;
        _apiClientMock.SendHeartbeatAsync(1, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                (Interlocked.Increment(ref beats) == 1 ? first : second).TrySetResult();
                return (ServerHeartbeatResponseDto?)null;
            });
        var service = CreateService(time, health);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        health.RequestHeartbeat();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        // The tick wait that the request cut short is still the one the loop waits on.
        time.Advance(TimeSpan.FromSeconds(1));
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(Volatile.Read(ref beats) >= 2);
    }

    [Fact]
    public async Task R1_8_AnInvalidatedMailInventory_IsCollectedAgainBeforeItsTtl_AndTheBeatCarriesTheNewOne()
    {
        // The clock never advances: only the invalidation can explain a second mail collection.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero));
        var health = new AgentRuntimeHealth(time);
        _mailCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(new MailDataDto { PostfixVersion = "before" }, new MailDataDto { PostfixVersion = "after" });
        var sent = new List<string>();
        _apiClientMock.SendHeartbeatAsync(1, Arg.Do<ServerHeartbeatDto>(beat => sent.Add(beat.Mail?.PostfixVersion ?? "")), Arg.Any<CancellationToken>())
            .Returns((ServerHeartbeatResponseDto?)null);
        var service = CreateService(time, health);
        var ct = TestContext.Current.CancellationToken;

        await service.CollectAndSendHeartbeatAsync(ct);
        await service.CollectAndSendHeartbeatAsync(ct);
        await _mailCollectorMock.Received(1).CollectAsync(Arg.Any<CancellationToken>());

        health.InvalidateInventories(CachedInventory.Mail);
        await service.CollectAndSendHeartbeatAsync(ct);

        await _mailCollectorMock.Received(2).CollectAsync(Arg.Any<CancellationToken>());
        Assert.Equal(["before", "before", "after"], sent);
        // The mark is consumed: the next beat within the TTL uses the cache again.
        await service.CollectAndSendHeartbeatAsync(ct);
        await _mailCollectorMock.Received(2).CollectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R119_AnInvalidatedApacheInventory_IsCollectedAgainBeforeItsTtl_AndTheBeatCarriesTheNewOne()
    {
        // The clock never advances: only the invalidation can explain a second Apache collection (a deleted
        // site file kept showing in the inventory until the 180 s cache expired).
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero));
        var health = new AgentRuntimeHealth(time);
        _apacheCollectorMock.CollectAsync(Arg.Any<CancellationToken>())
            .Returns(
                new ApacheDataDto { Version = "before" },
                new ApacheDataDto { Version = "after" });
        var sent = new List<string>();
        _apiClientMock.SendHeartbeatAsync(1, Arg.Do<ServerHeartbeatDto>(beat => sent.Add(beat.Apache?.Version ?? "")), Arg.Any<CancellationToken>())
            .Returns((ServerHeartbeatResponseDto?)null);
        var service = CreateService(time, health);
        var ct = TestContext.Current.CancellationToken;

        await service.CollectAndSendHeartbeatAsync(ct);
        await service.CollectAndSendHeartbeatAsync(ct);
        await _apacheCollectorMock.Received(1).CollectAsync(Arg.Any<CancellationToken>());

        health.InvalidateInventories(CachedInventory.Apache);
        await service.CollectAndSendHeartbeatAsync(ct);

        await _apacheCollectorMock.Received(2).CollectAsync(Arg.Any<CancellationToken>());
        Assert.Equal(["before", "before", "after"], sent);
        // The mark is consumed: the next beat within the TTL uses the cache again.
        await service.CollectAndSendHeartbeatAsync(ct);
        await _apacheCollectorMock.Received(2).CollectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ApiFailure_RetriesOnNextTimeProviderTick()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero));
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        _apiClientMock.SendHeartbeatAsync(1, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1)
                {
                    firstAttempt.TrySetResult();
                    throw new HttpRequestException("Connection refused");
                }

                secondAttempt.TrySetResult();
                return (ServerHeartbeatResponseDto?)null;
            });
        var service = CreateService(time);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.LoopStarted.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task ExecuteAsync_NotEnrolled_DoesNotSendHeartbeat()
    {
        var notEnrolledState = new AgentState(TimeProvider.System);
        var notEnrolled = CreateNotEnrolledEnrollmentService(notEnrolledState);

        var service = new HeartbeatService(
            _apiClientMock,
            notEnrolled,
            _metricsCollectorMock,
            _serviceCollectorMock,
            _dockerCollectorMock,
            _apacheCollectorMock,
            _certbotCollectorMock,
            _mailCollectorMock,
            _teamspeakCollectorMock,
            _portsentryCollectorMock,
            _rkhunterCollectorMock,
            _securityUpdatesCollectorMock,
            _firewallCollectorMock,
            _listeningPortsCollectorMock,
            _sudoersHashCollectorMock,
            _sshConfigCollectorMock,
            _dockerStorageMaintenanceMock,
            Substitute.For<IShellRunner>(),
            notEnrolledState,
            new AgentRuntimeHealth(TimeProvider.System),
            TimeProvider.System,
            _options,
            NullLogger<HeartbeatService>.Instance);

        // While not enrolled the loop parks on its wait-gate and never reaches a send.
        // Start then immediately stop: the gate is never crossed - deterministic, no sleep.
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await cts.CancelAsync();
        await service.StopAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.DidNotReceive().SendHeartbeatAsync(Arg.Any<int>(), Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_CollectorFailure_RecoversOnNextIteration()
    {
        _metricsCollectorMock.Collect()
            .Returns(
                _ => throw new InvalidOperationException("Collector error"),
                _ => new ServerHeartbeatDto { CpuPercent = 50, MemoryUsedMb = 2048, MemoryTotalMb = 8192 });

        var service = CreateService();

        // A collector throwing must not poison the loop: the next iteration collects and sends.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken));
        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.Received(1).SendHeartbeatAsync(1, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_HungInventory_SendsDegradedHeartbeatWithinBudget()
    {
        var hungCollector = new TaskCompletionSource<DockerDataDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dockerCollectorMock.CollectAllAsync(Arg.Any<CancellationToken>()).Returns(hungCollector.Task);
        var service = CreateService();

        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        await _apiClientMock.Received(1).SendHeartbeatAsync(1, Arg.Is<ServerHeartbeatDto>(heartbeat =>
                heartbeat.CapabilityDiagnostics.Any(diagnostic => diagnostic.Contains("timed out", StringComparison.OrdinalIgnoreCase))),
            Arg.Any<CancellationToken>());
        hungCollector.TrySetCanceled(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_PermanentlyHungInventory_IsQuarantinedSingleFlight()
    {
        var hungCollector = new TaskCompletionSource<DockerDataDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dockerCollectorMock.CollectAllAsync(Arg.Any<CancellationToken>()).Returns(hungCollector.Task);
        var service = CreateService();

        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);
        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        await _dockerCollectorMock.Received(1).CollectAllAsync(Arg.Any<CancellationToken>());
        await _apiClientMock.Received(2).SendHeartbeatAsync(1, Arg.Is<ServerHeartbeatDto>(heartbeat =>
                heartbeat.CapabilityDiagnostics.Any(diagnostic =>
                    diagnostic.Contains("quarantined", StringComparison.OrdinalIgnoreCase))),
            Arg.Any<CancellationToken>());
        hungCollector.TrySetCanceled(TestContext.Current.CancellationToken);
    }

    /// <summary> A storage measurement that runs for minutes (`docker system df` during a build) is
    /// off the beat. The beat is neither delayed nor degraded by it, reports no timeout nor
    /// quarantine, starts it only once, and still carries the live build state the deployment-only
    /// alert reads. </summary>
    [Fact]
    public async Task CollectAndSendHeartbeatAsync_HungStorageMeasurement_DoesNotDegradeTheBeat()
    {
        var hungMeasurement = new TaskCompletionSource<StorageDiagnosticsDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dockerStorageMaintenanceMock.CollectDiagnosticsAsync(Arg.Any<CancellationToken>()).Returns(hungMeasurement.Task);
        var buildAttempt = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
        _dockerStorageMaintenanceMock.BuildActive.Returns(true);
        _dockerStorageMaintenanceMock.DeploymentOnly.Returns(true);
        _dockerStorageMaintenanceMock.LastBuildAttemptAtUtc.Returns(buildAttempt);
        var service = CreateService();

        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);
        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        await _dockerStorageMaintenanceMock.Received(1).CollectDiagnosticsAsync(Arg.Any<CancellationToken>());
        await _apiClientMock.Received(2).SendHeartbeatAsync(1, Arg.Is<ServerHeartbeatDto>(heartbeat =>
                !heartbeat.CapabilityDiagnostics.Any(HeartbeatCollectionDiagnostics.IsCollectionDiagnostic)
                && heartbeat.StorageDiagnostics.BuildActive
                && heartbeat.StorageDiagnostics.DeploymentOnly
                && heartbeat.StorageDiagnostics.LastBuildAttemptAtUtc == buildAttempt
                && heartbeat.StorageDiagnostics.CollectedAtUtc == default),
            Arg.Any<CancellationToken>());
        hungMeasurement.TrySetCanceled(TestContext.Current.CancellationToken);
    }

    /// <summary> Once the background measurement completes, the next beat reports it, and it is not
    /// started again before <see cref="AgentRuntimeDefaults.StorageDiagnosticsTtl"/> has passed.
    /// </summary>
    [Fact]
    public async Task CollectAndSendHeartbeatAsync_CompletedStorageMeasurement_IsReportedAndKeptForItsTtl()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        _dockerStorageMaintenanceMock.CollectDiagnosticsAsync(Arg.Any<CancellationToken>())
            .Returns(new StorageDiagnosticsDto { BuildCacheBytes = 1234 });
        var service = CreateService(time);

        // The beat does not wait for the measurement it starts (see the hung case above); a later beat reports it.
        await service.CollectAndSendHeartbeatAsync(ct);
        for (var beat = 0; beat < 100 && LastSentHeartbeat().StorageDiagnostics.BuildCacheBytes != 1234; beat++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
            await service.CollectAndSendHeartbeatAsync(ct);
        }
        Assert.Equal(1234, LastSentHeartbeat().StorageDiagnostics.BuildCacheBytes);

        time.Advance(AgentRuntimeDefaults.StorageDiagnosticsTtl - TimeSpan.FromMinutes(1));
        await service.CollectAndSendHeartbeatAsync(ct);
        Assert.Equal(1234, LastSentHeartbeat().StorageDiagnostics.BuildCacheBytes);
        Assert.Equal(1, StorageMeasurementCount());

        time.Advance(TimeSpan.FromMinutes(1));
        await service.CollectAndSendHeartbeatAsync(ct);
        Assert.True(SpinWait.SpinUntil(() => StorageMeasurementCount() == 2, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_DockerProbeTimesOutUnderLoad_KeepsDockerAvailableForABoundedTime()
    {
        // R-31: during the nightly the daemon was busy (builds, image loads, Gitleaks container) and
        // `docker info` exceeded the probe timeout. That timeout used to be cached as "no Docker" for a
        // whole slow-inventory period, and the backend then refused the container-isolated stages.
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 3, 0, 0, TimeSpan.Zero));
        var daemonBusy = false;
        _shellRunnerMock.RunExecAsync(
                Arg.Is<string>(file => file.Contains("docker", StringComparison.OrdinalIgnoreCase)),
                Arg.Is<IReadOnlyList<string>>(arguments => arguments.Contains("info")),
                Arg.Any<CancellationToken>(),
                Arg.Any<TimeSpan?>())
            .Returns(_ => daemonBusy
                ? Task.FromException<ShellExecResult>(new OperationCanceledException("probe timeout"))
                : Task.FromResult(new ShellExecResult(0, "27.3.1", "")));
        var service = CreateService(time);

        await service.CollectAndSendHeartbeatAsync(ct);
        Assert.True(LastSentHeartbeat().DockerAvailable);

        daemonBusy = true;
        time.Advance(AgentRuntimeDefaults.SlowInventoryTtl + TimeSpan.FromSeconds(1));
        await service.CollectAndSendHeartbeatAsync(ct);
        Assert.True(LastSentHeartbeat().DockerAvailable);
        Assert.Contains(AgentCapabilities.DockerExecution, LastSentHeartbeat().AgentCapabilities!);
        Assert.Contains(AgentCapabilities.DockerManagement, LastSentHeartbeat().AgentCapabilities!);

        // A daemon that stays silent past the hold is reported unavailable (fail-closed).
        time.Advance(AgentRuntimeDefaults.DockerProbeIndeterminateHold);
        await service.CollectAndSendHeartbeatAsync(ct);
        Assert.False(LastSentHeartbeat().DockerAvailable);
        // R-119: without Docker the agent claims none of the Docker page's changes either.
        Assert.DoesNotContain(AgentCapabilities.DockerManagement, LastSentHeartbeat().AgentCapabilities!);
    }

    private ServerHeartbeatDto LastSentHeartbeat() => _apiClientMock.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IServerApiClient.SendHeartbeatAsync))
        .Select(call => (ServerHeartbeatDto)call.GetArguments()[1]!)
        .Last();

    private int StorageMeasurementCount() => _dockerStorageMaintenanceMock.ReceivedCalls()
        .Count(call => call.GetMethodInfo().Name == nameof(IDockerStorageMaintenance.CollectDiagnosticsAsync));

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_HungScannerProbe_StillReportsEmbeddedManifestIdentity()
    {
        var hungProbe = new TaskCompletionSource<ShellExecResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shellRunnerMock.RunExecAsync(
                Arg.Is<string>(file => file.Contains("docker", StringComparison.OrdinalIgnoreCase)),
                Arg.Is<IReadOnlyList<string>>(arguments => arguments.Contains("info")),
                Arg.Any<CancellationToken>(),
                Arg.Any<TimeSpan?>())
            .Returns(hungProbe.Task);
        var service = CreateService();

        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        var identity = $"scanner-manifest:sha256:{ScannerManifestCatalog.Sha256}";
        await _apiClientMock.Received(1).SendHeartbeatAsync(
            1,
            Arg.Is<ServerHeartbeatDto>(heartbeat => heartbeat.ScannerCapabilities.Contains(identity, StringComparer.Ordinal)),
            Arg.Any<CancellationToken>());
        hungProbe.TrySetCanceled(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_ReportsThePreviousExchangeAsClockSample()
    {
        // PLAN-010 lot 1: the agent's clock reads 2 s more than the backend's; each leg of the
        // exchange takes 100 ms. The second beat carries the first exchange's four instants.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero));
        var agentAhead = TimeSpan.FromSeconds(2);
        var sent = new List<ServerHeartbeatDto>();
        _apiClientMock.SendHeartbeatAsync(1, Arg.Do<ServerHeartbeatDto>(sent.Add), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                time.Advance(TimeSpan.FromMilliseconds(100));
                var backendNow = time.GetUtcNow().UtcDateTime - agentAhead;
                time.Advance(TimeSpan.FromMilliseconds(100));
                return new ServerHeartbeatResponseDto { ReceivedAtUtc = backendNow, RepliedAtUtc = backendNow };
            });
        var service = CreateService(time);
        var firstSentAt = time.GetUtcNow().UtcDateTime;

        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);
        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, sent.Count);
        Assert.Null(sent[0].ClockSample);
        var sample = Assert.IsType<ClockSampleDto>(sent[1].ClockSample);
        Assert.Equal(firstSentAt, sample.AgentSentAtUtc);
        Assert.Equal(firstSentAt.AddMilliseconds(200), sample.AgentReceivedAtUtc);
        Assert.Equal(firstSentAt.AddMilliseconds(100) - agentAhead, sample.BackendReceivedAtUtc);
        var estimate = sample.Estimate()!.Value;
        Assert.Equal(2_000, estimate.OffsetMs);
        Assert.Equal(100, estimate.UncertaintyMs);
    }

    [Fact]
    public async Task CollectAndSendHeartbeatAsync_UnstampedReplyOrFailedSend_LeavesNoClockSample()
    {
        var sent = new List<ServerHeartbeatDto>();
        _apiClientMock.SendHeartbeatAsync(1, Arg.Do<ServerHeartbeatDto>(sent.Add), Arg.Any<CancellationToken>())
            .Returns(
                _ => new ServerHeartbeatResponseDto { ReceivedAtUtc = DateTime.UtcNow, RepliedAtUtc = DateTime.UtcNow },
                _ => throw new HttpRequestException("Connection refused"),
                _ => new ServerHeartbeatResponseDto(),
                _ => new ServerHeartbeatResponseDto());
        var service = CreateService();

        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken));
        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);
        await service.CollectAndSendHeartbeatAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(sent[1].ClockSample);
        // The failed send's exchange has no reply: the next beat reports none rather than an old one.
        Assert.Null(sent[2].ClockSample);
        // A backend that does not stamp its reply (one predating the measure) gives no sample.
        Assert.Null(sent[3].ClockSample);
    }

    /// <summary>How many times the collector behind each cached inventory has been asked to collect.</summary>
    private Dictionary<CachedInventory, int> CollectionCounts() => new()
    {
        [CachedInventory.Apache] = CollectCalls(_apacheCollectorMock),
        [CachedInventory.Mail] = CollectCalls(_mailCollectorMock),
        [CachedInventory.Certbot] = CollectCalls(_certbotCollectorMock),
        [CachedInventory.Teamspeak] = CollectCalls(_teamspeakCollectorMock),
        [CachedInventory.Portsentry] = CollectCalls(_portsentryCollectorMock),
        [CachedInventory.Rkhunter] = CollectCalls(_rkhunterCollectorMock),
        [CachedInventory.Firewall] = CollectCalls(_firewallCollectorMock),
        [CachedInventory.SecurityUpdates] = CollectCalls(_securityUpdatesCollectorMock),
        [CachedInventory.Docker] = CollectCalls(_dockerCollectorMock)
    };

    private static int CollectCalls(object collector) =>
        collector.ReceivedCalls().Count(call => call.GetMethodInfo().Name is "CollectAsync" or "CollectAllAsync");

    [Theory]
    [InlineData(CachedInventory.Certbot)]
    [InlineData(CachedInventory.Teamspeak)]
    [InlineData(CachedInventory.Portsentry)]
    [InlineData(CachedInventory.Rkhunter)]
    [InlineData(CachedInventory.Firewall)]
    [InlineData(CachedInventory.SecurityUpdates)]
    [InlineData(CachedInventory.Docker)]
    public async Task R119_AnInvalidatedInventory_IsCollectedAgainBeforeItsTtl_AndNoOtherOneIs(CachedInventory outdated)
    {
        // The clock never advances: only the invalidation can explain a second collection of this inventory.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
        var health = new AgentRuntimeHealth(time);
        _apiClientMock.SendHeartbeatAsync(1, Arg.Any<ServerHeartbeatDto>(), Arg.Any<CancellationToken>())
            .Returns((ServerHeartbeatResponseDto?)null);
        var service = CreateService(time, health);
        var ct = TestContext.Current.CancellationToken;

        await service.CollectAndSendHeartbeatAsync(ct);
        await service.CollectAndSendHeartbeatAsync(ct);
        var before = CollectionCounts();
        Assert.Equal(1, before[outdated]);

        health.InvalidateInventories(outdated);
        await service.CollectAndSendHeartbeatAsync(ct);

        var after = CollectionCounts();
        foreach (var inventory in before.Keys)
            Assert.Equal(inventory == outdated ? 2 : 1, after[inventory]);
        // The mark is consumed: the next beat within the TTL uses the cache again.
        await service.CollectAndSendHeartbeatAsync(ct);
        Assert.Equal(2, CollectionCounts()[outdated]);
    }

    private HeartbeatService CreateService(
        TimeProvider? timeProvider = null,
        AgentRuntimeHealth? runtimeHealth = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        return new(
        _apiClientMock,
        _enrollment,
        _metricsCollectorMock,
        _serviceCollectorMock,
        _dockerCollectorMock,
        _apacheCollectorMock,
        _certbotCollectorMock,
        _mailCollectorMock,
        _teamspeakCollectorMock,
        _portsentryCollectorMock,
        _rkhunterCollectorMock,
        _securityUpdatesCollectorMock,
        _firewallCollectorMock,
        _listeningPortsCollectorMock,
        _sudoersHashCollectorMock,
        _sshConfigCollectorMock,
        _dockerStorageMaintenanceMock,
        _shellRunnerMock,
        _agentState,
        runtimeHealth ?? new AgentRuntimeHealth(time),
        time,
        _options,
        NullLogger<HeartbeatService>.Instance);
    }

    private EnrollmentService CreateEnrolledEnrollmentService()
    {
        var mockConfig = Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>();
        return new EnrollmentService(
            _apiClientMock,
            _options,
            _agentState,
            mockConfig,
            Substitute.For<ICredentialProtector>(),
            Substitute.For<IShellRunner>(),
            TimeProvider.System,
            NullLogger<EnrollmentService>.Instance);
    }

    private EnrollmentService CreateNotEnrolledEnrollmentService(AgentState state) =>
        new(
            _apiClientMock,
            _options,
            state,
            Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>(),
            Substitute.For<ICredentialProtector>(),
            Substitute.For<IShellRunner>(),
            TimeProvider.System,
            NullLogger<EnrollmentService>.Instance);
}
