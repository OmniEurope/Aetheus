// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Services;

/// <summary>
/// R-41: publishes the host's system release (<c>/etc/os-release</c>) and its installed dpkg packages for the CVE
/// watch, at start and then once a day, and again after a system upgrade so a fixed package stops being reported.
/// Read-only and without privilege: <c>dpkg-query -W</c> reads the package database every user may read. A host that is
/// not Linux, has no os-release, or whose dpkg gives an empty or failed list publishes nothing: the server then stays
/// "not reported" on the back, never "nothing found". A host without dpkg on a system of another family (an RPM host)
/// publishes its release alone, flagged unsupported, so the back says "system not covered". A failed send is retried an
/// hour later.
/// </summary>
public sealed class HostPackageInventoryReporter(
    IServerApiClient apiClient,
    IEnrollmentService enrollment,
    AgentState agentState,
    AgentRuntimeHealth runtimeHealth,
    IShellRunner shell,
    TimeProvider timeProvider,
    ILogger<HostPackageInventoryReporter> logger) : BackgroundService
{
    internal static readonly TimeSpan ReportInterval = TimeSpan.FromDays(1);
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    // Leaves the first heartbeat and the task polling the start of the agent's life.
    internal static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

    private const string RunningKernelPath = "/proc/sys/kernel/osrelease";

    internal bool IsLinux { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>Reads a file, or null when it is absent; replaced by the tests.</summary>
    internal Func<string, string?> ReadFile { get; init; } = path => File.Exists(path) ? File.ReadAllText(path) : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsLinux) return;
        while (!enrollment.IsEnrolled && !stoppingToken.IsCancellationRequested)
            await Task.Delay(AgentRuntimeDefaults.StartupRetryDelay, timeProvider, stoppingToken).ConfigureAwait(false);

        var wait = StartDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await WaitAsync(wait, stoppingToken).ConfigureAwait(false)) return;
            try
            {
                wait = await ReportAsync(stoppingToken).ConfigureAwait(false) ? ReportInterval : RetryInterval;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let the watch take the agent down: the next attempt comes in an hour.
                logger.LogWarning(ex, "Host package report failed, will retry later");
                wait = RetryInterval;
            }
        }
    }

    /// <summary>Waits the delay, or less when a system upgrade asked for a new report. False once stopping.</summary>
    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var timer = Task.Delay(delay, timeProvider, waitCts.Token);
        var requested = runtimeHealth.WaitForHostPackageReportRequestAsync(waitCts.Token);
        await Task.WhenAny(timer, requested).ConfigureAwait(false);
        await waitCts.CancelAsync().ConfigureAwait(false);
        return !stoppingToken.IsCancellationRequested;
    }

    /// <summary>Collects and sends one report. True when it was sent, or when the host has nothing the watch can read
    /// (no need to retry before tomorrow); false when it should be tried again soon.</summary>
    internal async Task<bool> ReportAsync(CancellationToken ct)
    {
        if (agentState.ServerId is not { } serverId) return false;
        var report = await CollectAsync(ct).ConfigureAwait(false);
        if (report is null) return true;
        await apiClient.ReportHostPackagesAsync(serverId, report, ct).ConfigureAwait(false);
        logger.LogInformation("Host package report sent: {Count} packages, {OsId} {OsVersionId}", report.Packages.Count, report.OsId, report.OsVersionId);
        return true;
    }

    internal async Task<HostPackageInventoryReportDto?> CollectAsync(CancellationToken ct)
    {
        var release = OsReleaseFile.Paths.Select(ReadFile).FirstOrDefault(text => text is not null);
        if (OsReleaseFile.Identify(OsReleaseFile.Parse(release)) is not { } os)
        {
            logger.LogDebug("No usable os-release file: the host system is not reported");
            return null;
        }

        ShellExecResult result;
        try
        {
            result = await shell.RunExecAsync("dpkg-query", DpkgHostPackages.QueryArgv, ct, QueryTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // dpkg-query absent: the launch itself throws. On a system of another family (an RPM host) that is the
            // expected answer, said as such so the server reads "system not covered"; on a Debian-family one it is a
            // broken host, and nothing is sent rather than a report that could pass for a clean one.
            if (DpkgHostPackages.IsDpkgFamily(os.Id))
            {
                logger.LogWarning(ex, "dpkg-query is unavailable on a {OsId} host: the host packages are not reported", os.Id);
                return null;
            }

            logger.LogDebug(ex, "dpkg-query is unavailable on a {OsId} host: its system is reported as not covered", os.Id);
            return Report(os, packageManagerSupported: false, []);
        }

        var packages = result.ExitCode == 0 && !result.Truncated ? DpkgHostPackages.Parse(result.StdOut) : [];
        // An empty or failed read is not a host with nothing installed, and a list over the bound would be cut: neither is sent.
        if (packages.Count == 0 || packages.Count > HostPackageLimits.MaxPackages)
        {
            logger.LogWarning("dpkg-query gave no usable package list (exit {ExitCode}, {Count} packages): nothing reported", result.ExitCode, packages.Count);
            return null;
        }

        return Report(os, packageManagerSupported: true, packages, ReadRunningKernel());
    }

    /// <summary>The booted kernel's release (<c>/proc/sys/kernel/osrelease</c>, what <c>uname -r</c> prints), read as a file:
    /// null when it cannot be read, and the back then matches every installed kernel.</summary>
    private string? ReadRunningKernel()
    {
        try
        {
            var release = ReadFile(RunningKernelPath)?.Trim();
            return string.IsNullOrEmpty(release) ? null : release;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "The running kernel release could not be read: every installed kernel is matched");
            return null;
        }
    }

    private static HostPackageInventoryReportDto Report(
        (string Id, string? VersionId, string? Version, string? PrettyName) os, bool packageManagerSupported, List<HostPackageDto> packages,
        string? runningKernel = null) =>
        new()
        {
            RunningKernelRelease = Bounded(runningKernel, HostPackageLimits.MaxKernelReleaseLength),
            OsId = Bounded(os.Id, HostPackageLimits.MaxOsIdLength)!,
            OsVersionId = Bounded(os.VersionId, HostPackageLimits.MaxOsIdLength),
            OsVersion = Bounded(os.Version, HostPackageLimits.MaxOsVersionLength),
            OsPrettyName = Bounded(os.PrettyName, HostPackageLimits.MaxOsPrettyNameLength),
            PackageManagerSupported = packageManagerSupported,
            Packages = packages
        };

    private static string? Bounded(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
