// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Agent.Core.Services;

/// <summary>
/// File-backed <see cref="IAgentExitJournal"/> in the agent work directory. Two files carry the answer:
/// <c>.agent-running.json</c> is written when a process starts, and <c>.agent-last-exit.json</c> when a
/// process ends on purpose. A start that finds an exit record reports it; a start that finds only the
/// running marker reports <see cref="AgentExitKinds.Unclean"/> (the previous process had no chance to
/// write one); a start that finds neither is a first install and reports nothing. The report is kept on
/// disk until the backend confirms it, so a crash before the first heartbeat does not lose it.
/// </summary>
internal sealed class AgentExitJournal(
    IOptions<AetheusAgentOptions> options,
    TimeProvider timeProvider,
    Func<string?> readBootId,
    ILogger<AgentExitJournal> logger) : IAgentExitJournal
{
    internal const string RunningFileName = ".agent-running.json";
    internal const string LastExitFileName = ".agent-last-exit.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private readonly string _workDirectory = options.Value.WorkDirectory;
    private bool _started;
    private bool _exitRecorded;
    private AgentExitReportDto? _pending;

    internal string RunningPath => Path.Combine(_workDirectory, RunningFileName);
    internal string LastExitPath => Path.Combine(_workDirectory, LastExitFileName);

    /// <summary>The previous process's host boot id and exit, as stored on disk.</summary>
    private sealed record ExitRecord(string Kind, string? Reason, DateTime? OccurredAtUtc, string? BootId);

    private sealed record RunningRecord(DateTime StartedAtUtc, string? BootId);

    public AgentExitReportDto? GetPendingReport()
    {
        lock (_gate)
        {
            EnsureStarted();
            return _pending;
        }
    }

    public void MarkReported()
    {
        lock (_gate)
        {
            EnsureStarted();
            if (_pending is null)
                return;
            _pending = null;
            // This process already wrote its own exit: that file is not the one just reported.
            if (_exitRecorded)
                return;
            TryDelete(LastExitPath);
        }
    }

    public void RecordExit(string kind, string reason)
    {
        lock (_gate)
        {
            if (_exitRecorded)
                return;
            _exitRecorded = true;
            var record = new ExitRecord(
                kind, Truncate(reason), timeProvider.GetUtcNow().UtcDateTime, readBootId());
            TryWrite(LastExitPath, record);
        }
    }

    private void EnsureStarted()
    {
        if (_started)
            return;
        _started = true;

        var exit = TryRead<ExitRecord>(LastExitPath);
        if (exit is null)
        {
            var running = TryRead<RunningRecord>(RunningPath);
            if (running is not null)
            {
                exit = new ExitRecord(
                    AgentExitKinds.Unclean,
                    Truncate(
                        $"No exit record: the previous process (started {running.StartedAtUtc:u}) ended without " +
                        "writing one (killed, out of memory, crash without a handler, or power loss)."),
                    null,
                    running.BootId);
                // Persisted like an exit record, so the report survives a crash before it is sent.
                TryWrite(LastExitPath, exit);
            }
        }

        _pending = exit is null
            ? null
            : new AgentExitReportDto
            {
                Kind = exit.Kind,
                Reason = exit.Reason,
                OccurredAtUtc = exit.OccurredAtUtc,
                PreviousBootId = exit.BootId,
                BootId = readBootId()
            };
        TryWrite(RunningPath, new RunningRecord(timeProvider.GetUtcNow().UtcDateTime, readBootId()));
    }

    private static string Truncate(string reason) =>
        reason.Length <= AgentExitReportDto.MaxReasonLength
            ? reason
            : reason[..AgentExitReportDto.MaxReasonLength];

    private T? TryRead<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged record is as good as none: the start falls back to the next source of evidence.
            logger.LogWarning(ex, "Ignoring unreadable agent exit journal file {Path}", path);
            TryDelete(path);
            return null;
        }
    }

    private void TryWrite<T>(string path, T record)
    {
        try
        {
            Directory.CreateDirectory(_workDirectory);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A journal that cannot be written must never stop the agent or its exit.
            logger.LogWarning(ex, "Could not write agent exit journal file {Path}", path);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete agent exit journal file {Path}", path);
        }
    }

    /// <summary>The host boot id, which changes when the machine reboots; null where the platform has none.</summary>
    internal static string? ReadHostBootId()
    {
        const string path = "/proc/sys/kernel/random/boot_id";
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
