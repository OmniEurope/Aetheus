// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Collectors.Ssh;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Services;

public sealed class HeartbeatService(
    IServerApiClient apiClient,
    IEnrollmentService enrollment,
    IMetricsCollector metricsCollector,
    IServiceCollector serviceCollector,
    IDockerCollector dockerCollector,
    IApacheCollector apacheCollector,
    ICertbotCollector certbotCollector,
    IMailCollector mailCollector,
    ITeamspeakCollector teamspeakCollector,
    IPortsentryCollector portsentryCollector,
    IRkhunterCollector rkhunterCollector,
    ISecurityUpdatesCollector securityUpdatesCollector,
    IFirewallCollector firewallCollector,
    IListeningPortsCollector listeningPortsCollector,
    ISudoersHashCollector sudoersHashCollector,
    ISshConfigCollector sshConfigCollector,
    IDockerStorageMaintenance dockerStorageMaintenance,
    IShellRunner shell,
    AgentState agentState,
    AgentRuntimeHealth runtimeHealth,
    TimeProvider timeProvider,
    IOptions<AetheusAgentOptions> options,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    private readonly AetheusAgentOptions _options = options.Value;
    private readonly TaskCompletionSource _loopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task LoopStarted => _loopStarted.Task;
    private readonly TimeSpan _collectionTimeout = TimeSpan.FromSeconds(
        Math.Clamp(options.Value.HeartbeatCollectionTimeoutSeconds, 1, 60));
    private static readonly string AgentVersion =
        typeof(HeartbeatService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    // Resolved once at startup - the install timestamp can't change while the agent is
    // running; rereading the marker on every tick would add an FS round-trip per heartbeat.
    private readonly DateTime? _agentInstalledAt = InstallInfoReader.Read(options.Value.WorkDirectory);

    // Cache slow inventory collectors by TTL while retaining CPU, RAM and service sampling per
    // heartbeat. Docker uses a shorter TTL for timely container-state updates.
    private static readonly TimeSpan DockerInventoryTtl = AgentRuntimeDefaults.DockerInventoryTtl;
    private static readonly TimeSpan SlowInventoryTtl = AgentRuntimeDefaults.SlowInventoryTtl;

    private readonly CachedCollector<List<ServiceInfoDto>> _servicesCache = new(
        TimeSpan.Zero, serviceCollector.CollectAsync, timeProvider, static () => [], logger, "services");
    private readonly CachedCollector<DockerDataDto> _dockerCache = new(
        DockerInventoryTtl, dockerCollector.CollectAllAsync, timeProvider, static () => new DockerDataDto(), logger, "docker");
    private readonly CachedCollector<ApacheDataDto> _apacheCache = new(
        SlowInventoryTtl, apacheCollector.CollectAsync, timeProvider, static () => new ApacheDataDto(), logger, "apache");
    private readonly CachedCollector<CertbotDataDto> _certbotCache = new(
        SlowInventoryTtl, certbotCollector.CollectAsync, timeProvider, static () => new CertbotDataDto(), logger, "certbot");
    private readonly CachedCollector<MailDataDto> _mailCache = new(
        SlowInventoryTtl, mailCollector.CollectAsync, timeProvider, static () => new MailDataDto(), logger, "mail");
    private readonly CachedCollector<TeamspeakDataDto> _teamspeakCache = new(
        SlowInventoryTtl, teamspeakCollector.CollectAsync, timeProvider, static () => new TeamspeakDataDto(), logger, "teamspeak");
    private readonly CachedCollector<PortsentryDataDto> _portsentryCache = new(
        SlowInventoryTtl, portsentryCollector.CollectAsync, timeProvider, static () => new PortsentryDataDto(), logger, "portsentry");
    private readonly CachedCollector<RkhunterDataDto> _rkhunterCache = new(
        SlowInventoryTtl, rkhunterCollector.CollectAsync, timeProvider, static () => new RkhunterDataDto(), logger, "rkhunter");
    private readonly CachedCollector<SecurityUpdatesDataDto> _securityUpdatesCache = new(
        SlowInventoryTtl, securityUpdatesCollector.CollectAsync, timeProvider, static () => new SecurityUpdatesDataDto(), logger, "security updates");
    private readonly CachedCollector<FirewallDataDto> _firewallCache = new(
        SlowInventoryTtl, firewallCollector.CollectAsync, timeProvider, static () => new FirewallDataDto(), logger, "firewall");
    // Same slow-inventory rhythm as the other module probes. The on-demand scan
    // (OperationKind.PortsObserve) is what an operator uses when that period is too long to wait.
    private readonly CachedCollector<List<ObservedPortDto>> _listeningPortsCache = new(
        SlowInventoryTtl, listeningPortsCollector.CollectAsync, timeProvider, static () => [], logger, "listening ports");
    private readonly CachedCollector<Dictionary<string, string>> _sudoersCache = new(
        SlowInventoryTtl, sudoersHashCollector.CollectAsync, timeProvider, static () => new Dictionary<string, string>(), logger, "sudoers");
    private readonly CachedCollector<SshConfigDataDto> _sshConfigCache = new(
        SlowInventoryTtl, sshConfigCollector.CollectAsync, timeProvider, static () => new SshConfigDataDto(), logger, "ssh configuration");
    private readonly CachedCollector<bool> _dockerAvailableCache = new(
        SlowInventoryTtl, ct => DockerProbe.IsAvailableAsync(shell, ct), timeProvider, static () => false, logger, "docker availability");
    private readonly CachedCollector<bool> _pipelineRunnerAvailableCache = new(
        SlowInventoryTtl, ct => PipelineRunnerProbe.IsAvailableAsync(shell, ct), timeProvider, static () => false, logger, "pipeline runner");
    private readonly CachedCollector<bool> _deploymentAvailableCache = new(
        SlowInventoryTtl, ct => DeploymentCapabilityProbe.IsAvailableAsync(shell, ct), timeProvider, static () => false, logger, "deployment capability");
    private readonly CachedCollector<List<string>> _capDiagnosticsCache = new(
        SlowInventoryTtl, ct => CapabilityDiagnosticsProbe.CollectAsync(shell, ct), timeProvider, static () => [], logger, "capability diagnostics");
    private readonly CachedCollector<List<string>> _scannerDiagnosticsCache = new(
        SlowInventoryTtl, ct => ScannerCapabilityProbe.CollectAsync(shell, options.Value.WorkDirectory, timeProvider, ct),
        timeProvider, static () => [], logger, "scanner capability diagnostics");
    // Run expensive storage measurements independently of the heartbeat budget and report the
    // latest completed result with its collection timestamp. Background refresh is not cancelled by
    // a heartbeat timeout.
    private readonly CachedCollector<StorageDiagnosticsDto> _storageDiagnosticsCache = new(
        AgentRuntimeDefaults.StorageDiagnosticsTtl, dockerStorageMaintenance.CollectDiagnosticsAsync, timeProvider,
        static () => new StorageDiagnosticsDto(), logger, "storage diagnostics");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!enrollment.IsEnrolled && !stoppingToken.IsCancellationRequested)
            await Task.Delay(AgentRuntimeDefaults.StartupRetryDelay, timeProvider, stoppingToken).ConfigureAwait(false);

        runtimeHealth.BeginHeartbeatGracePeriod();
        logger.LogInformation("Heartbeat service started (interval: {Interval}s)", _options.HeartbeatIntervalSeconds);

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.HeartbeatIntervalSeconds),
            timeProvider);
        _loopStarted.TrySetResult();

        if (await TryCollectAndSendHeartbeatAsync(stoppingToken).ConfigureAwait(false))
            logger.LogInformation("Initial heartbeat sent; agent contract is ready");
        if (stoppingToken.IsCancellationRequested)
            return;

        while (await WaitForNextBeatAsync(timer, stoppingToken).ConfigureAwait(false))
            await TryCollectAndSendHeartbeatAsync(stoppingToken).ConfigureAwait(false);
    }

    // PLAN-010 lot 1: the previous exchange's four instants, reported with the next beat (the reply's
    // arrival time only exists once the beat it measures has been sent). A failed send leaves none.
    private ClockSampleDto? _lastClockSample;

    // The timer accepts one waiter at a time, so a tick wait cut short by a request is kept for the
    // next iteration instead of being started again.
    private Task<bool>? _pendingTick;

    /// <summary>Send the next heartbeat on the timer tick or immediately when
    /// AgentRuntimeHealth.RequestHeartbeat requests it after a service action.</summary>
    private async Task<bool> WaitForNextBeatAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        _pendingTick ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();
        using var requestWait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var requested = runtimeHealth.WaitForHeartbeatRequestAsync(requestWait.Token);
        if (await Task.WhenAny(_pendingTick, requested).ConfigureAwait(false) == requested)
        {
            await requested.ConfigureAwait(false);
            return true;
        }

        await requestWait.CancelAsync().ConfigureAwait(false);
        try
        {
            await requested.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The abandoned wait for a request: the tick came first.
        }

        var ticked = await _pendingTick.ConfigureAwait(false);
        _pendingTick = null;
        return ticked;
    }

    private async Task<bool> TryCollectAndSendHeartbeatAsync(CancellationToken stoppingToken)
    {
        try
        {
            await CollectAndSendHeartbeatAsync(stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Heartbeat failed, will retry next interval");
            return false;
        }
    }

    // E-3: one iteration's work, extracted so tests can drive it deterministically
    // without a PeriodicTimer or a real Task.Delay sleep.
    internal async Task CollectAndSendHeartbeatAsync(CancellationToken ct)
    {
        var heartbeat = metricsCollector.Collect();
        // A mail operation just completed, the cached mail inventory is collected again.
        if (runtimeHealth.ConsumeMailInventoryInvalidation())
            _mailCache.Invalidate();

        using var collectorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var collectorToken = collectorCts.Token;
        var servicesTask = _servicesCache.GetAsync(collectorToken);
        var dockerTask = _dockerCache.GetAsync(collectorToken);
        var apacheTask = _apacheCache.GetAsync(collectorToken);
        var certbotTask = _certbotCache.GetAsync(collectorToken);
        var mailTask = _mailCache.GetAsync(collectorToken);
        var teamspeakTask = _teamspeakCache.GetAsync(collectorToken);
        var portsentryTask = _portsentryCache.GetAsync(collectorToken);
        var rkhunterTask = _rkhunterCache.GetAsync(collectorToken);
        var securityUpdatesTask = _securityUpdatesCache.GetAsync(collectorToken);
        var firewallTask = _firewallCache.GetAsync(collectorToken);
        var listeningPortsTask = _listeningPortsCache.GetAsync(collectorToken);
        var sudoersTask = _sudoersCache.GetAsync(collectorToken);
        var sshConfigTask = _sshConfigCache.GetAsync(collectorToken);
        var dockerAvailableTask = _dockerAvailableCache.GetAsync(collectorToken);
        var pipelineRunnerAvailableTask = _pipelineRunnerAvailableCache.GetAsync(collectorToken);
        var deploymentAvailableTask = _deploymentAvailableCache.GetAsync(collectorToken);
        var capDiagnosticsTask = _capDiagnosticsCache.GetAsync(collectorToken);
        var scannerDiagnosticsTask = _scannerDiagnosticsCache.GetAsync(collectorToken);
        // Stopping token, not the collector token: the beat neither waits for this refresh nor cancels it.
        var storageDiagnosticsTask = _storageDiagnosticsCache.GetAsync(ct);
        var allCollectors = Task.WhenAll(
            servicesTask, dockerTask, apacheTask, certbotTask, mailTask, teamspeakTask,
            portsentryTask, rkhunterTask, securityUpdatesTask, firewallTask, listeningPortsTask, sudoersTask,
            dockerAvailableTask, pipelineRunnerAvailableTask, capDiagnosticsTask, scannerDiagnosticsTask);
        allCollectors = Task.WhenAll(allCollectors, deploymentAvailableTask, sshConfigTask);

        var timeoutTask = Task.Delay(_collectionTimeout, timeProvider, ct);
        var collectorsCompleted = await Task.WhenAny(allCollectors, timeoutTask).ConfigureAwait(false) == allCollectors;
        if (collectorsCompleted)
        {
            await allCollectors.ConfigureAwait(false);
        }
        else
        {
            await collectorCts.CancelAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            logger.LogWarning(
                "Heartbeat inventory collection exceeded {TimeoutSeconds}s; sending cached/degraded data",
                _collectionTimeout.TotalSeconds);
            _ = ObserveLateCollectorsAsync(allCollectors);
        }

        var capabilityDiagnostics = CompletedValueOr(
            capDiagnosticsTask, _capDiagnosticsCache.LastValueOr(new List<string>()));
        if (!collectorsCompleted)
        {
            capabilityDiagnostics = [.. capabilityDiagnostics, HeartbeatCollectionDiagnostics.CollectionTimedOut];
        }
        var quarantinedCollectors = GetQuarantinedCollectorNames();
        if (quarantinedCollectors.Count > 0)
        {
            capabilityDiagnostics =
            [
                .. capabilityDiagnostics,
                HeartbeatCollectionDiagnostics.Quarantined(quarantinedCollectors)
            ];
        }

        var dockerAvailable = CompletedValueOr(
            dockerAvailableTask, _dockerAvailableCache.LastValueOr(false));
        bool? pipelineRunnerAvailable = !_pipelineRunnerAvailableCache.IsCollectionInFlight
            && pipelineRunnerAvailableTask.Status == TaskStatus.RanToCompletion
                ? pipelineRunnerAvailableTask.Result
                : null;
        var sudoersHashes = CompletedValueOr(
            sudoersTask, _sudoersCache.LastValueOr(new Dictionary<string, string>()));
        var deploymentAvailable = CompletedValueOr(
            deploymentAvailableTask, _deploymentAvailableCache.LastValueOr(false));
        if (sudoersHashes.ContainsKey("aetheus-deploy") && !deploymentAvailable)
        {
            capabilityDiagnostics =
            [
                .. capabilityDiagnostics,
                "deployment grant is installed but its versioned functional probe failed; deployment.apply is disabled"
            ];
        }

        heartbeat = heartbeat with
        {
            AgentSessionId = agentState.SessionId,
            AgentVersion = AgentVersion,
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilities = BuildEffectiveCapabilities(
                dockerAvailable, pipelineRunnerAvailable, deploymentAvailable, sudoersHashes),
            AgentInstalledAt = _agentInstalledAt,
            DockerAvailable = dockerAvailable,
            PipelineRunnerAvailable = pipelineRunnerAvailable,
            // S-DES-23: report the dev-only TLS-bypass mode so the UI can flag it on the server card.
            InsecureTls = _options.AllowInsecureCerts,
            Services = CompletedValueOr(servicesTask, _servicesCache.LastValueOr([])),
            Docker = CompletedValueOr(dockerTask, _dockerCache.LastValueOr(new DockerDataDto())),
            Apache = CompletedValueOr(apacheTask, _apacheCache.LastValueOr(new ApacheDataDto())),
            Certbot = CompletedValueOr(certbotTask, _certbotCache.LastValueOr(new CertbotDataDto())),
            Mail = CompletedValueOr(mailTask, _mailCache.LastValueOr(new MailDataDto())),
            Teamspeak = CompletedValueOr(teamspeakTask, _teamspeakCache.LastValueOr(new TeamspeakDataDto())),
            Portsentry = CompletedValueOr(portsentryTask, _portsentryCache.LastValueOr(new PortsentryDataDto())),
            Rkhunter = CompletedValueOr(rkhunterTask, _rkhunterCache.LastValueOr(new RkhunterDataDto())),
            SecurityUpdates = CompletedValueOr(
                securityUpdatesTask, _securityUpdatesCache.LastValueOr(new SecurityUpdatesDataDto())),
            Firewall = CompletedValueOr(firewallTask, _firewallCache.LastValueOr(new FirewallDataDto())),
            // Reported only when this beat's scan actually completed. Falling back to the cached list
            // would restate an old scan as a fresh one, and the backend replaces the whole observed
            // source with what it receives - stale data would read as "these ports are listening now".
            ObservedPorts = listeningPortsTask.Status == TaskStatus.RanToCompletion
                ? listeningPortsTask.Result
                : [],
            ObservedPortsAvailable = listeningPortsTask.Status == TaskStatus.RanToCompletion,
            SudoersHashes = sudoersHashes,
            SudoersInventoryAvailable = _sudoersCache.HasValue,
            // Null until one collection completed: the backend must not take an empty default for a reading.
            SshConfig = _sshConfigCache.HasValue
                ? CompletedValueOr(sshConfigTask, _sshConfigCache.LastValueOr(new SshConfigDataDto()))
                : null,
            CapabilityDiagnostics = capabilityDiagnostics,
            ScannerCapabilities = EnsureScannerManifestIdentity(CompletedValueOr(
                scannerDiagnosticsTask, _scannerDiagnosticsCache.LastValueOr(new List<string>()))),
            StorageDiagnostics = WithLiveBuildState(CompletedValueOr(
                storageDiagnosticsTask, _storageDiagnosticsCache.LastValueOr(new StorageDiagnosticsDto())))
        };

        heartbeat = heartbeat with { ClockSample = _lastClockSample };
        _lastClockSample = null;
        // T1 on the wall clock; the round trip on the monotonic clock, so a clock step during the
        // exchange does not distort it. Retries of the resilience handler fall inside the interval
        // and widen the uncertainty; they cannot bias the estimate.
        var sentAt = timeProvider.GetUtcNow().UtcDateTime;
        var sendStarted = timeProvider.GetTimestamp();
        var response = await apiClient.SendHeartbeatAsync(agentState.ServerId!.Value, heartbeat, ct).ConfigureAwait(false);
        _lastClockSample = ClockSampleDto.FromExchange(
            sentAt, timeProvider.GetElapsedTime(sendStarted), response?.ReceivedAtUtc, response?.RepliedAtUtc);
        AgentUpdateRecoveryState.ConfirmSuccessfulHeartbeat(_options.WorkDirectory);
        runtimeHealth.MarkHeartbeatSuccess();
        await ApplyTokenRenewalAsync(response, ct).ConfigureAwait(false);

        logger.LogDebug("Heartbeat sent - CPU: {Cpu}%, RAM: {Ram}/{Total} MB",
            heartbeat.CpuPercent, heartbeat.MemoryUsedMb, heartbeat.MemoryTotalMb);
    }

    private static List<string> BuildEffectiveCapabilities(
        bool dockerAvailable,
        bool? pipelineRunnerAvailable,
        bool deploymentAvailable,
        IReadOnlyDictionary<string, string> sudoersHashes)
    {
        var capabilities = new HashSet<string>(
            AgentCapabilities.SoftwareCapabilities,
            StringComparer.Ordinal)
        {
            // Published by this agent rather than by the shared software list: only an agent that
            // carries the function executor may receive type: aetheus tasks.
            AgentCapabilities.PipelineFunctions
        };

        // Build functions work on a checked-out workspace, exactly like pipeline.build.
        if (pipelineRunnerAvailable is not true)
        {
            capabilities.Remove(AgentCapabilities.PipelineBuild);
            capabilities.Remove(AgentCapabilities.PipelineFunctions);
        }
        if (dockerAvailable)
            capabilities.Add(AgentCapabilities.DockerExecution);

        // Deployment functions act on a live environment: same grant and same probe as deployment.apply.
        if (sudoersHashes.ContainsKey("aetheus-deploy") && deploymentAvailable)
        {
            capabilities.Add(AgentCapabilities.Deployment);
            capabilities.Add(AgentCapabilities.DeploymentFunctions);
        }
        AddGrantCapability("aetheus-package", AgentCapabilities.PackageManagement);
        AddGrantCapability("aetheus-patch", AgentCapabilities.PatchManagement);
        AddGrantCapability("aetheus-firewall", AgentCapabilities.FirewallManagement);
        AddGrantCapability("aetheus-apache", AgentCapabilities.ApacheManagement);
        AddGrantCapability("aetheus-certbot", AgentCapabilities.CertbotManagement);
        AddGrantCapability("aetheus-mail", AgentCapabilities.MailManagement);
        AddGrantCapability("aetheus-teamspeak", AgentCapabilities.TeamspeakManagement);
        AddGrantCapability("aetheus-portsentry", AgentCapabilities.PortsentryManagement);
        AddGrantCapability("aetheus-cron", AgentCapabilities.CronManagement);
        AddGrantCapability("aetheus-service-enable", AgentCapabilities.ServiceManagement);

        return capabilities.Order(StringComparer.Ordinal).ToList();

        void AddGrantCapability(string grant, string capability)
        {
            if (sudoersHashes.ContainsKey(grant))
                capabilities.Add(capability);
        }
    }

    private static T CompletedValueOr<T>(Task<T> task, T fallback) =>
        task.Status == TaskStatus.RanToCompletion ? task.Result : fallback;

    // The measured sizes may be minutes old; the build state must not be, because the backend raises
    // the deployment-only build alert from it. It costs nothing to read, so every beat reads it live.
    private StorageDiagnosticsDto WithLiveBuildState(StorageDiagnosticsDto measured) => measured with
    {
        BuilderName = dockerStorageMaintenance.BuilderName,
        DeploymentOnly = dockerStorageMaintenance.DeploymentOnly,
        BuildActive = dockerStorageMaintenance.BuildActive,
        LastBuildAttemptAtUtc = dockerStorageMaintenance.LastBuildAttemptAtUtc
    };

    private static List<string> EnsureScannerManifestIdentity(IEnumerable<string> diagnostics)
    {
        var reported = diagnostics.ToList();
        var identity = $"scanner-manifest:sha256:{ScannerManifestCatalog.Sha256}";
        if (!reported.Contains(identity, StringComparer.Ordinal))
            reported.Add(identity);
        return reported;
    }

    private List<string> GetQuarantinedCollectorNames()
    {
        (string Name, bool InFlight)[] collectors =
        [
            ("services", _servicesCache.IsCollectionInFlight),
            ("docker", _dockerCache.IsCollectionInFlight),
            ("apache", _apacheCache.IsCollectionInFlight),
            ("certbot", _certbotCache.IsCollectionInFlight),
            ("mail", _mailCache.IsCollectionInFlight),
            ("teamspeak", _teamspeakCache.IsCollectionInFlight),
            ("portsentry", _portsentryCache.IsCollectionInFlight),
            ("rkhunter", _rkhunterCache.IsCollectionInFlight),
            ("security updates", _securityUpdatesCache.IsCollectionInFlight),
            ("firewall", _firewallCache.IsCollectionInFlight),
            ("sudoers", _sudoersCache.IsCollectionInFlight),
            ("ssh configuration", _sshConfigCache.IsCollectionInFlight),
            ("docker availability", _dockerAvailableCache.IsCollectionInFlight),
            ("pipeline runner", _pipelineRunnerAvailableCache.IsCollectionInFlight),
            ("capability diagnostics", _capDiagnosticsCache.IsCollectionInFlight),
            ("scanner capability diagnostics", _scannerDiagnosticsCache.IsCollectionInFlight)
        ];
        return collectors.Where(collector => collector.InFlight).Select(collector => collector.Name).ToList();
    }

    private async Task ObserveLateCollectorsAsync(Task collectors)
    {
        try
        {
            await collectors.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected after the collection budget expires.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Late heartbeat collector failed after degraded heartbeat was sent");
        }
    }

    private async Task ApplyTokenRenewalAsync(ServerHeartbeatResponseDto? response, CancellationToken ct)
    {
        if (response is null || string.IsNullOrEmpty(response.RenewedToken)) return;
        // Persist FIRST; only swap the in-memory token once the new one is safely
        // on disk. If the persist throws, the old token is still valid until its
        // own server-side expiry (the server does not revoke it) - fail-safe.
        await enrollment.PersistRenewedTokenAsync(response.RenewedToken, ct).ConfigureAwait(false);
        agentState.BearerToken = response.RenewedToken;
        agentState.TokenExpiresAt = response.RenewedTokenExpiresAtUtc;
        apiClient.SetBearerToken(response.RenewedToken);
        logger.LogInformation("Agent Bearer token auto-renewed (expires {ExpiresAt:u})",
            response.RenewedTokenExpiresAtUtc);
    }

    // TTL cache plus a single-flight quarantine. A collector that ignores cancellation keeps
    // its one in-flight call, while later heartbeats immediately use cached/degraded data.
    private sealed class CachedCollector<T>(
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> collect,
        TimeProvider time,
        Func<T> fallback,
        ILogger logger,
        string name)
    {
        private readonly object _sync = new();
        private T? _value;
        private DateTimeOffset? _refreshedAt;
        private Task<T>? _inFlight;
        // Invalidate marks the value outdated; a collection started before it does not clear the
        // mark.
        private bool _stale;
        private int _generation;

        public Task<T> GetAsync(CancellationToken ct)
        {
            lock (_sync)
            {
                var now = time.GetUtcNow();
                if (!_stale && _refreshedAt is not null && now - _refreshedAt < ttl && _value is not null)
                    return Task.FromResult(_value);

                if (_inFlight is not null)
                    return Task.FromResult(_value is null ? fallback() : _value);

                _inFlight = CollectAndCacheAsync(_generation, ct);
                return _inFlight;
            }
        }

        /// <summary>The next <see cref="GetAsync"/> collects again, whatever the TTL left; the last value
        /// is still reported while that collection runs.</summary>
        public void Invalidate()
        {
            lock (_sync)
            {
                _stale = true;
                _generation++;
            }
        }

        public T LastValueOr(T fallbackValue)
        {
            lock (_sync)
                return _refreshedAt is null || _value is null ? fallbackValue : _value;
        }

        public bool IsCollectionInFlight
        {
            get
            {
                lock (_sync)
                    return _inFlight is not null;
            }
        }

        public bool HasValue
        {
            get
            {
                lock (_sync)
                    return _refreshedAt is not null;
            }
        }

        private async Task<T> CollectAndCacheAsync(int generation, CancellationToken ct)
        {
            await Task.Yield();
            try
            {
                var collected = await collect(ct).ConfigureAwait(false);
                lock (_sync)
                {
                    _value = collected;
                    _refreshedAt = time.GetUtcNow();
                    if (generation == _generation)
                        _stale = false;
                }
                return collected;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return LastValueOr(fallback());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Heartbeat collector {Collector} failed; using cached/degraded data", name);
                return LastValueOr(fallback());
            }
            finally
            {
                lock (_sync)
                    _inFlight = null;
            }
        }
    }
}
