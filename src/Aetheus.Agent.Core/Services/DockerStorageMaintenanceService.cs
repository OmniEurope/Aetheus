// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Aetheus.Agent.Core.Services;

/// <summary>
/// Owns one named Buildx cache per agent. It never invokes a global prune, removes an image, container
/// or volume, and serializes maintenance with builds through one exclusive gate.
/// </summary>
public sealed class DockerStorageMaintenanceService(
    IShellRunner shell,
    IOptions<AetheusAgentOptions> options,
    TimeProvider timeProvider,
    ILogger<DockerStorageMaintenanceService> logger) : BackgroundService, IDockerStorageMaintenance
{
    private const int MaxDiagnosticFiles = 1_000_000;
    private readonly AetheusAgentOptions _agentOptions = options.Value;
    private readonly DockerStorageMaintenanceOptions _options = options.Value.DockerStorageMaintenance;
    private readonly NuGetCacheMaintenance _nugetMaintenance =
        new(options.Value.DockerStorageMaintenance, timeProvider, logger);
    private readonly SemaphoreSlim _exclusiveGate = new(1, 1);
    private readonly Channel<bool> _buildCompletions = Channel.CreateUnbounded<bool>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly string _pendingCompletionPath = Path.Combine(
        options.Value.WorkDirectory,
        ".docker-maintenance-pending");
    private bool _buildGateHeld;
    private long _lastBuildAttemptUtcTicks;

    public string BuilderName => ResolveBuilderName(_options.BuilderName, _agentOptions.Name);
    public bool DeploymentOnly => _options.DeploymentOnly;
    public bool BuildActive => _buildGateHeld;

    public DateTime? LastBuildAttemptAtUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastBuildAttemptUtcTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    private bool IsDryRun => _options.DryRun;

    public bool IsBuildCommand(string command)
    {
        var result = DockerBuildCommandClassifier.Classify(command, _options.DeploymentOnly);
        if (result.TimedOut)
        {
            logger.LogWarning(
                "Docker build classification timed out for a {CommandLength}-character command; treating it as a build",
                command?.Length ?? 0);
        }
        return result.IsBuild;
    }

    /// <summary>
    /// Acquires the exclusive build lease and prepares the workspace. Returns false only when
    /// <paramref name="waitBudget"/> elapses before the lease frees, so the caller can hand its task back
    /// to the queue instead of holding it in Assigned past the control plane's start ceiling. A null
    /// budget waits indefinitely, which is the historical behaviour.
    /// </summary>
    public async Task<bool> PrepareBuildAsync(
        IDictionary<string, string> environmentVariables,
        CancellationToken ct,
        bool runDockerMaintenance = true,
        TimeSpan? waitBudget = null)
    {
        Interlocked.Exchange(ref _lastBuildAttemptUtcTicks, timeProvider.GetUtcNow().UtcDateTime.Ticks);
        if (_options.DeploymentOnly && !_options.AllowBuildsOnDeploymentTarget)
        {
            throw new InvalidOperationException(
                "Docker build refused: this agent is configured as a deployment-only target. " +
                "Use a dedicated build runner or explicitly audit and enable AllowBuildsOnDeploymentTarget.");
        }

        if (!await _exclusiveGate.WaitAsync(waitBudget ?? Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false))
            return false;
        _buildGateHeld = true;
        try
        {
            await RecoverPendingCompletionUnderBuildLockAsync(ct).ConfigureAwait(false);
            environmentVariables["AETHEUS_BUILDX_BUILDER"] = BuilderName;
            environmentVariables["BUILDX_BUILDER"] = BuilderName;
            if (!_options.Enabled || !runDockerMaintenance) return true;

            await EnsureBuilderAsync(ct).ConfigureAwait(false);
            var (usedPercent, freeBytes) = GetDiskState();
            if (ShouldUseAggressivePolicy(usedPercent, freeBytes, _options))
                await RunMaintenanceUnderLockAsync("pre-build disk pressure", ct).ConfigureAwait(false);

            (_, freeBytes) = GetDiskState();
            if (!IsDryRun && freeBytes < GiB(_options.MinFreeSpaceGiB))
            {
                throw new InvalidOperationException(
                    $"Docker build refused after bounded cache maintenance: only {FormatBytes(freeBytes)} free; " +
                    $"the configured safety floor is {_options.MinFreeSpaceGiB} Go.");
            }
        }
        catch
        {
            ReleaseBuildGate();
            throw;
        }

        return true;
    }

    public async Task CompleteBuildAsync(CancellationToken ct, bool runDockerMaintenance = true)
    {
        _ = await CompleteBuildCoreAsync(ct, runDockerMaintenance).ConfigureAwait(false);
    }

    private async Task<bool> CompleteBuildCoreAsync(
        CancellationToken ct,
        bool runDockerMaintenance,
        bool clearPendingMarker = false)
    {
        if (!_buildGateHeld)
            return true;
        var completed = false;
        try
        {
            if (_options.Enabled && runDockerMaintenance)
                await RunMaintenanceUnderLockAsync("post-build finalization", ct).ConfigureAwait(false);
            completed = true;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A completed build remains truthful. Cleanup failure is observable but never rewrites its result.
            logger.LogError(ex, "Docker storage maintenance failed after build; build result is preserved");
            return false;
        }
        finally
        {
            if (completed && clearPendingMarker)
                DeletePendingCompletionMarker();
            ReleaseBuildGate();
        }
    }

    public async Task ScheduleBuildCompletionAsync(
        bool runDockerMaintenance = true,
        CancellationToken ct = default)
    {
        if (!_buildGateHeld)
            return;
        try
        {
            var directory = Path.GetDirectoryName(_pendingCompletionPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                _pendingCompletionPath,
                runDockerMaintenance ? "docker" : "lease",
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                ex,
                "Could not persist pending post-build maintenance marker {MarkerPath}; "
                + "the in-process queue remains active",
                _pendingCompletionPath);
        }
        await _buildCompletions.Writer.WriteAsync(runDockerMaintenance, ct).ConfigureAwait(false);
    }

    public async Task RunMaintenanceAsync(string reason, CancellationToken ct)
    {
        if (!_options.Enabled) return;
        if (!await _exclusiveGate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
        {
            logger.LogInformation("Skipping Docker storage maintenance ({Reason}): a build or cleanup owns the lock", reason);
            return;
        }

        try
        {
            await RunMaintenanceUnderLockAsync(reason, ct).ConfigureAwait(false);
        }
        finally
        {
            _exclusiveGate.Release();
        }
    }

    public async Task<StorageDiagnosticsDto> CollectDiagnosticsAsync(CancellationToken ct)
    {
        var builder = await InspectBuilderBytesAsync(ct).ConfigureAwait(false);
        long images = 0;
        long containers = 0;
        long volumes = 0;
        var docker = await shell.RunExecAsync("docker",
            ["system", "df", "--format", "{{.Type}}\t{{.Size}}\t{{.Reclaimable}}"],
            ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (docker.ExitCode == 0)
        {
            foreach (var line in docker.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split('\t', StringSplitOptions.TrimEntries);
                if (parts.Length < 2) continue;
                var bytes = ParseSizeBytes(parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
                if (parts[0].Equals("Images", StringComparison.OrdinalIgnoreCase)) images = bytes;
                else if (parts[0].Equals("Containers", StringComparison.OrdinalIgnoreCase)) containers = bytes;
                else if (parts[0].Contains("Volumes", StringComparison.OrdinalIgnoreCase)) volumes = bytes;
            }
        }

        var nugetPath = _nugetMaintenance.PackagesPath;
        var workDirectory = await GetPathSizeAsync(_agentOptions.WorkDirectory, ct).ConfigureAwait(false);
        var installDirectory = await GetPathSizeAsync(AppContext.BaseDirectory, ct).ConfigureAwait(false);
        var nugetCache = await GetPathSizeAsync(nugetPath, ct).ConfigureAwait(false);
        var partialPaths = new List<string>(3);
        if (!workDirectory.IsComplete) partialPaths.Add("agent-work-directory");
        if (!installDirectory.IsComplete) partialPaths.Add("agent-install-directory");
        if (!nugetCache.IsComplete) partialPaths.Add("nuget-cache");
        return new StorageDiagnosticsDto
        {
            BuilderName = BuilderName,
            DryRun = IsDryRun,
            DeploymentOnly = _options.DeploymentOnly,
            BuildActive = BuildActive,
            LastBuildAttemptAtUtc = LastBuildAttemptAtUtc,
            BuildCacheAvailable = builder.Available,
            DockerInventoryAvailable = docker.ExitCode == 0,
            BuildCacheBytes = builder.Total,
            BuildCacheReclaimableBytes = builder.Reclaimable,
            DockerImagesBytes = images,
            DockerContainersBytes = containers,
            DockerVolumesBytes = volumes,
            AgentWorkDirectoryBytes = workDirectory.Bytes,
            AgentInstallDirectoryBytes = installDirectory.Bytes,
            NuGetCacheBytes = nugetCache.Bytes,
            PartialPathMeasurements = partialPaths,
            JournalBytes = await GetJournalSizeAsync(ct).ConfigureAwait(false),
            CollectedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ResumePendingBuildCompletionAsync(stoppingToken).ConfigureAwait(false);
        var interval = TimeSpan.FromMinutes(
            Math.Clamp(_options.MaintenanceIntervalMinutes, 5, 1440));
        while (!stoppingToken.IsCancellationRequested)
        {
            var completionReady = _buildCompletions.Reader.WaitToReadAsync(stoppingToken).AsTask();
            var periodicReady = Task.Delay(interval, timeProvider, stoppingToken);
            try
            {
                var winner = await Task.WhenAny(completionReady, periodicReady).ConfigureAwait(false);
                if (winner == completionReady && await completionReady.ConfigureAwait(false))
                {
                    while (_buildCompletions.Reader.TryRead(out var runDockerMaintenance))
                        await CompleteQueuedBuildAsync(runDockerMaintenance, stoppingToken)
                            .ConfigureAwait(false);
                }
                else if (_options.Enabled)
                {
                    await RunMaintenanceAsync("periodic", stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Periodic Docker storage maintenance failed");
            }
        }
    }

    private async Task ResumePendingBuildCompletionAsync(CancellationToken ct)
    {
        if (!File.Exists(_pendingCompletionPath))
            return;
        // The loop starts asynchronously: a build of this process may already hold the lock and have
        // queued its own completion, which only this loop can read. Waiting for that lock here would
        // deadlock; the queued completion releases it, and a marker left by a previous process is
        // recovered by the next build under its lock.
        if (!await _exclusiveGate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false))
            return;
        _buildGateHeld = true;
        var marker = await ReadPendingCompletionMarkerAsync(_pendingCompletionPath, ct).ConfigureAwait(false);
        if (marker is null)
        {
            ReleaseBuildGate();
            return;
        }
        await CompleteQueuedBuildAsync(
            string.Equals(marker.Trim(), "docker", StringComparison.Ordinal),
            ct).ConfigureAwait(false);
    }

    private async Task RecoverPendingCompletionUnderBuildLockAsync(CancellationToken ct)
    {
        if (!File.Exists(_pendingCompletionPath))
            return;
        var marker = await ReadPendingCompletionMarkerAsync(_pendingCompletionPath, ct).ConfigureAwait(false);
        if (marker is null)
            return;
        if (string.Equals(marker.Trim(), "docker", StringComparison.Ordinal) && _options.Enabled)
            await RunMaintenanceUnderLockAsync("recovered post-build finalization", ct)
                .ConfigureAwait(false);
        File.Delete(_pendingCompletionPath);
    }

    private async Task CompleteQueuedBuildAsync(bool runDockerMaintenance, CancellationToken ct)
    {
        await CompleteBuildCoreAsync(ct, runDockerMaintenance, clearPendingMarker: true).ConfigureAwait(false);
    }

    private void DeletePendingCompletionMarker()
    {
        try
        {
            File.Delete(_pendingCompletionPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                ex,
                "Could not remove completed post-build maintenance marker {MarkerPath}",
                _pendingCompletionPath);
        }
    }

    internal static async Task<string?> ReadPendingCompletionMarkerAsync(
        string path,
        CancellationToken ct)
    {
        try
        {
            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static bool ShouldUseAggressivePolicy(
        int usedPercent, long freeBytes, DockerStorageMaintenanceOptions options) =>
        usedPercent >= options.PressureUsedPercent || freeBytes < GiB(options.MinFreeSpaceGiB);

    internal static string ResolveBuilderName(string? configured, string? agentName)
    {
        var source = string.IsNullOrWhiteSpace(configured)
            ? $"aetheus-{(string.IsNullOrWhiteSpace(agentName) ? Environment.MachineName : agentName)}"
            : configured;
        var normalized = Regex.Replace(source!.ToLowerInvariant(), "[^a-z0-9_.-]+", "-").Trim('-', '.');
        if (normalized.Length == 0) normalized = "aetheus-agent";
        return normalized.Length <= 63 ? normalized : normalized[..63].TrimEnd('-', '.');
    }

    internal static long ParseSizeBytes(string value)
    {
        var match = Regex.Match(value.Trim(), @"^(?<n>[0-9]+(?:[.,][0-9]+)?)\s*(?<u>[kmgtp](?:i?b)?|bytes?)?\*?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success) return 0;
        var number = double.Parse(match.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var multiplier = SizeUnitMultipliers.TryGetValue(match.Groups["u"].Value.ToUpperInvariant(), out var unit) ? unit : 1d;
        return checked((long)(number * multiplier));
    }

    /// <summary>Decimal (K, KB...) and binary (KIB...) size units; anything else, bytes included, is 1.</summary>
    private static readonly System.Collections.Frozen.FrozenDictionary<string, double> SizeUnitMultipliers =
        System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new Dictionary<string, double>
        {
            ["K"] = 1_000d,
            ["KB"] = 1_000d,
            ["KIB"] = 1_024d,
            ["M"] = 1_000_000d,
            ["MB"] = 1_000_000d,
            ["MIB"] = 1_048_576d,
            ["G"] = 1_000_000_000d,
            ["GB"] = 1_000_000_000d,
            ["GIB"] = 1_073_741_824d,
            ["T"] = 1_000_000_000_000d,
            ["TB"] = 1_000_000_000_000d,
            ["TIB"] = 1_099_511_627_776d,
            ["P"] = 1_000_000_000_000_000d,
            ["PB"] = 1_000_000_000_000_000d,
            ["PIB"] = 1_125_899_906_842_624d
        }, StringComparer.Ordinal);

    private async Task EnsureBuilderAsync(CancellationToken ct)
    {
        var inspect = await shell.RunExecAsync(
            "docker", ["buildx", "inspect", BuilderName], ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (inspect.ExitCode == 0) return;
        if (IsDryRun)
        {
            logger.LogWarning("Buildx builder {Builder} is absent; dry-run will not create it", BuilderName);
            return;
        }

        var create = await shell.RunExecAsync(
            "docker", ["buildx", "create", "--name", BuilderName, "--driver", "docker-container"],
            ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (create.ExitCode != 0)
            throw new InvalidOperationException($"Could not create dedicated Buildx builder '{BuilderName}': {create.StdErr.Trim()}");
        logger.LogInformation("Created dedicated Buildx builder {Builder}", BuilderName);
    }

    private async Task RunMaintenanceUnderLockAsync(string reason, CancellationToken ct)
    {
        if (_options.DeploymentOnly && !_options.AllowBuildsOnDeploymentTarget)
        {
            logger.LogInformation(
                "Skipping Buildx cache maintenance ({Reason}): agent is deployment-only and builds are forbidden",
                reason);
            await _nugetMaintenance.RunAsync(IsDryRun, ct).ConfigureAwait(false);
            return;
        }

        var started = timeProvider.GetTimestamp();
        var before = await InspectBuilderBytesAsync(ct).ConfigureAwait(false);
        if (!before.Available && !IsDryRun)
            throw new InvalidOperationException($"Could not inventory dedicated Buildx builder '{BuilderName}'; refusing cache mutation.");
        var (usedPercent, freeBytes) = GetDiskState();
        var aggressive = ShouldUseAggressivePolicy(usedPercent, freeBytes, _options);
        var ageHours = aggressive ? _options.PressureCacheAgeHours : _options.MaxCacheAgeHours;

        logger.LogInformation(
            "Docker storage decision: builder={Builder}, reason={Reason}, dryRun={DryRun}, diskUsed={UsedPercent}%, " +
            "free={Free}, cache={Cache}, reclaimable={Reclaimable}, maxAge={AgeHours}h",
            BuilderName, reason, IsDryRun, usedPercent, FormatBytes(freeBytes),
            FormatBytes(before.Total), FormatBytes(before.Reclaimable), ageHours);

        if (!IsDryRun)
        {
            var prune = await shell.RunExecAsync("docker",
                ["buildx", "prune", "--builder", BuilderName, "--force", "--filter", $"until={ageHours}h",
                 "--reserved-space", $"{_options.ReservedSpaceGiB}GB", "--max-used-space", $"{_options.MaxCacheGiB}GB",
                 "--min-free-space", $"{_options.MinFreeSpaceGiB}GB"],
                ct, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            if (prune.ExitCode != 0)
                throw new InvalidOperationException($"Buildx prune failed for '{BuilderName}': {prune.StdErr.Trim()}");
        }

        var after = IsDryRun ? before : await InspectBuilderBytesAsync(ct).ConfigureAwait(false);
        await _nugetMaintenance.RunAsync(IsDryRun, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Docker storage result: builder={Builder}, before={Before}, after={After}, reclaimed={Reclaimed}, durationMs={DurationMs}",
            BuilderName, FormatBytes(before.Total), FormatBytes(after.Total),
            FormatBytes(Math.Max(0, before.Total - after.Total)), timeProvider.GetElapsedTime(started).TotalMilliseconds);
    }

    private async Task<(bool Available, long Total, long Reclaimable)> InspectBuilderBytesAsync(CancellationToken ct)
    {
        var result = await shell.RunExecAsync(
            "docker", ["buildx", "du", "--builder", BuilderName], ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            logger.LogWarning("Could not inventory Buildx builder {Builder}: {Error}", BuilderName, result.StdErr.Trim());
            return (false, 0, 0);
        }

        long total = 0;
        long reclaimable = 0;
        foreach (var rawLine in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawLine.StartsWith("Total:", StringComparison.OrdinalIgnoreCase))
                total = ParseSizeBytes(rawLine["Total:".Length..]);
            else if (rawLine.StartsWith("Reclaimable:", StringComparison.OrdinalIgnoreCase))
                reclaimable = ParseSizeBytes(rawLine["Reclaimable:".Length..]);
        }
        return (true, total, reclaimable);
    }

    internal readonly record struct PathSizeMeasurement(long Bytes, bool IsComplete);

    private async Task<PathSizeMeasurement> GetPathSizeAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return new PathSizeMeasurement(0, true);
        if (!OperatingSystem.IsWindows())
            return await MeasureWithDuAsync(path, ct).ConfigureAwait(false);

        var measurement = MeasureManagedPathSize(path, MaxDiagnosticFiles, ct);
        if (!measurement.IsComplete)
            logger.LogWarning(
                "Storage measurement for {Path} is partial (enumeration failure or {Limit}-file safety limit)",
                path, MaxDiagnosticFiles);
        return measurement;
    }

    /// <summary>The Linux measurement: <c>du -sb</c>, whose first token is the size in bytes.</summary>
    internal async Task<PathSizeMeasurement> MeasureWithDuAsync(string path, CancellationToken ct)
    {
        var result = await shell.RunExecAsync("du", ["-sb", "--", path], ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        // IShellRunner implementations return a result, but test doubles and a defensive
        // third-party implementation may still violate that contract. Diagnostics must
        // degrade to an unknown size instead of taking the whole agent heartbeat down.
        if (result is { ExitCode: 0 })
        {
            var token = result.StdOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
                return new PathSizeMeasurement(bytes, true);
        }
        return new PathSizeMeasurement(0, false);
    }

    internal static PathSizeMeasurement MeasureManagedPathSize(
        string path,
        int fileLimit,
        CancellationToken ct)
    {
        long total = 0;
        try
        {
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", enumerationOptions))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > fileLimit)
                    return new PathSizeMeasurement(total, false);
                total = checked(total + new FileInfo(file).Length);
            }
            return new PathSizeMeasurement(total, true);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new PathSizeMeasurement(total, false);
        }
    }

    private async Task<long> GetJournalSizeAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return 0;
        var result = await shell.RunExecAsync("journalctl", ["--disk-usage"], ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (result is not { ExitCode: 0 }) return 0;
        var match = Regex.Match(result.StdOut, @"(?<size>[0-9]+(?:[.,][0-9]+)?\s*[KMGTPE](?:i?B)?)\s+in\s+(?:the\s+)?file\s*system",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success ? ParseSizeBytes(match.Groups["size"].Value) : 0;
    }

    private static (int UsedPercent, long FreeBytes) GetDiskState()
    {
        var root = OperatingSystem.IsWindows()
            ? Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"
            : "/";
        var drive = new DriveInfo(root);
        var usedPercent = drive.TotalSize <= 0
            ? 0
            : (int)Math.Round(100d * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize);
        return (usedPercent, drive.AvailableFreeSpace);
    }

    private void ReleaseBuildGate()
    {
        if (!_buildGateHeld) return;
        _buildGateHeld = false;
        _exclusiveGate.Release();
    }

    private static long GiB(int value) => StorageSizeFormatting.GiB(value);

    private static string FormatBytes(long bytes) => StorageSizeFormatting.FormatBytes(bytes);
}
