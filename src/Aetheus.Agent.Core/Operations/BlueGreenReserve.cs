// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

/// <summary>Keep the replaced colour running with its revision and exact served upstream
/// configuration for rapid reversal, at the cost of two running colours. The next deployment
/// recycles it, while explicit retirement stops it.</summary>
internal sealed class BlueGreenReserve(BlueGreenContext context)
{
    private string Dir => Path.Combine(context.StateDir, "reserve");
    private string ColourPath => Path.Combine(Dir, "colour");
    private string RevisionPath => Path.Combine(Dir, "revision");
    private string UpstreamPath => Path.Combine(Dir, "upstream.conf");

    internal string? Colour => BlueGreenJournal.ReadOrNull(ColourPath) is ("blue" or "green") and var colour ? colour : null;
    internal string Revision => BlueGreenJournal.ReadOrNull(RevisionPath) ?? string.Empty;
    internal string? Upstream => File.Exists(UpstreamPath) ? File.ReadAllText(UpstreamPath) : null;

    /// <summary>A reserve is usable only with the configuration that served it.</summary>
    internal bool Exists => Colour is not null && Upstream is not null;

    /// <summary>Records <paramref name="colour"/> as the reserve, replacing any previous record.</summary>
    internal void Record(string colour, string revision, string upstream)
    {
        Clear();
        Directory.CreateDirectory(Dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        BlueGreenJournal.WritePrivateAtomic(UpstreamPath, upstream);
        BlueGreenJournal.WritePrivateAtomic(RevisionPath, revision);
        // Written last: a record without its colour is not a reserve, so a crash mid-write leaves none.
        BlueGreenJournal.WritePrivateAtomic(ColourPath, colour);
    }

    /// <summary>
    /// At commit: keeps the colour the transaction replaced, with the configuration the switch
    /// snapshotted before moving traffic and the revision that colour was serving (read before the
    /// commit overwrites it). A first deployment replaced nothing and leaves no reserve.
    /// </summary>
    internal async Task KeepPreviousAsync(
        BlueGreenJournal journal, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var previous = journal.PreviousLive;
        var snapshot = Path.Combine(context.JournalDir, "upstream.before");
        if (previous is not ("blue" or "green") || !File.Exists(snapshot))
        {
            Clear();
            return;
        }
        var previousRevision = File.Exists(context.SourceCommitFile)
            ? (await File.ReadAllTextAsync(context.SourceCommitFile, ct).ConfigureAwait(false)).Trim()
            : string.Empty;
        Record(previous, previousRevision, await File.ReadAllTextAsync(snapshot, ct).ConfigureAwait(false));
        await onOutput($"##aetheus[setvariable name=BLUEGREEN_RESERVE_COLOR]{previous}", TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput($"The replaced {previous} colour keeps running in reserve for a return to N-1.", TaskLogLevel.Info)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// With no transaction open, <c>bluegreen-retire</c> ends the reserve: it stops that colour, never
    /// the live one. Null when there is no reserve to retire.
    /// </summary>
    internal async Task<ExecutorResult?> RetireAsync(
        BlueGreenJournal journal, BlueGreenCompose compose, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (Colour is not { } kept || kept == journal.ReadLiveColour()) return null;
        if (await compose.StopColourAsync(context, kept, timeoutSeconds, ct).ConfigureAwait(false) != 0)
        {
            await onOutput($"The {kept} colour kept in reserve could not be stopped.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }
        Clear();
        await onOutput($"The {kept} colour kept in reserve is stopped; no return to N-1 remains.", TaskLogLevel.Info)
            .ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    internal void Clear()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }
}
