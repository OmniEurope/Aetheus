// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Services;

/// <summary>Reclaims inactive run workspaces while preserving active work reported by the backend.
/// When backend state is unavailable, only workspaces older than FallbackRetention may be
/// removed.</summary>
public sealed class RunWorkspaceReaper(
    IServerApiClient api,
    IOptions<AetheusAgentOptions> options,
    TimeProvider timeProvider,
    ILogger<RunWorkspaceReaper> logger) : BackgroundService
{
    /// <summary>Only used when the backend is unreachable, so it is deliberately far longer than any
    /// plausible run: a queue backed up behind an approval must not be swept out from under itself.</summary>
    internal static readonly TimeSpan FallbackRetention = TimeSpan.FromDays(7);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly AetheusAgentOptions _options = options.Value;

    /// <summary>Sibling of the backend's workspace layout: {WorkDirectory}/w/{slot}/s.</summary>
    internal string WorkspaceRoot => Path.Combine(_options.WorkDirectory, "w");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A restart is exactly when workspaces are most likely to be orphaned (the run that owned them
        // may have been failed or cancelled while this agent was down), so sweep once on the way up.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let a sweep take the agent down: it is disk hygiene, not part of running work.
                logger.LogWarning(ex, "Run workspace sweep failed");
            }

            try
            {
                await Task.Delay(SweepInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        if (!Directory.Exists(WorkspaceRoot)) return 0;

        var activeSlots = await api.GetActiveWorkspaceSlotsAsync(ct).ConfigureAwait(false);
        var active = activeSlots is null
            ? null
            : new HashSet<string>(activeSlots, StringComparer.OrdinalIgnoreCase);

        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(WorkspaceRoot))
        {
            ct.ThrowIfCancellationRequested();
            if (ShouldReclaim(directory, active) && TryDelete(directory)) removed++;
        }

        if (removed > 0)
            logger.LogInformation(
                "Reclaimed {Count} stale run workspace(s) under {Root} ({Mode})",
                removed,
                WorkspaceRoot,
                active is null ? "age-based fallback, backend unreachable" : "run state");

        return removed;
    }

    private bool ShouldReclaim(string directory, HashSet<string>? activeSlots)
    {
        var slot = Path.GetFileName(directory);

        // Backend unreachable: fall back on age alone. LastWriteTime is refreshed by the run itself as
        // it works, so a long-running job keeps its own workspace alive without asking anyone.
        if (activeSlots is null)
            return LastActivityUtc(directory) < timeProvider.GetUtcNow().UtcDateTime - FallbackRetention;

        return !activeSlots.Contains(slot);
    }

    /// <summary>
    /// Newest timestamp of the directory or its immediate children. The root's own timestamp does not
    /// move when a file deeper inside is written, which would age out a workspace that is being used.
    /// </summary>
    private static DateTime LastActivityUtc(string directory)
    {
        var newest = Directory.GetLastWriteTimeUtc(directory);
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var written = File.GetLastWriteTimeUtc(entry);
                if (written > newest) newest = written;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: treat it as freshly touched so an unreadable directory is never deleted on age.
            return DateTime.MaxValue;
        }

        return newest;
    }

    private bool TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A build container may still hold a handle. Next sweep retries; the disk is not lost.
            logger.LogDebug(ex, "Could not reclaim run workspace {Directory}", directory);
            return false;
        }
    }
}
