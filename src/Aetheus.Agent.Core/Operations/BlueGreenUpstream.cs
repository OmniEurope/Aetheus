// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// The web-server configuration side of a blue-green cutover: rendering the upstream file that points
/// traffic at a colour, and putting the previous one back when a reload is rejected.
///
/// Rendering this file IS the switch; the reload only applies it. That is why a surviving placeholder
/// must stop the write rather than reach the web server, and why a restore reports whether it
/// actually succeeded instead of being assumed.
/// </summary>
internal static class BlueGreenUpstream
{
    /// <summary>
    /// Renders the template for one colour, or returns null when a placeholder survives so a
    /// half-substituted configuration can never be written.
    /// </summary>
    internal static string? Render(string template, string colour, int front, int back)
    {
        var rendered = template
            .Replace("#{FRONT_PORT}#", front.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("#{BACK_PORT}#", back.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("#{COLOR}#", colour, StringComparison.Ordinal)
            // The shell renderer these steps replace spells the same placeholder #{ACTIVE_COLOR}#, and
            // the versioned upstream templates in the repository use that spelling. Accepting both is
            // what lets a project migrate its pipeline without rewriting a template the remaining
            // shell fallback still renders - and a template keeping the other spelling would simply
            // fail the guard below rather than be written half-substituted.
            .Replace("#{ACTIVE_COLOR}#", colour, StringComparison.Ordinal);
        return rendered.Contains("#{", StringComparison.Ordinal) ? null : rendered;
    }

    /// <summary>
    /// Writes through a uniquely named temporary file then renames, so a crash mid-write cannot leave
    /// the web server a truncated configuration. The name carries a per-write component because the
    /// agent runs several tasks concurrently in one process.
    /// </summary>
    internal static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var temp = $"{path}.tmp.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(temp, content, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Reads the upstream file and the reload helper every traffic move needs, reporting whether both
    /// were supplied. The caller names what it could not do without them.
    /// </summary>
    internal static bool TryReadTargets(
        IReadOnlyDictionary<string, string> envVars, out string confPath, out string reloadCommand)
    {
        confPath = envVars.GetValueOrDefault("AETHEUS_BG_UPSTREAM_CONF", string.Empty).Trim();
        reloadCommand = envVars.GetValueOrDefault("AETHEUS_BG_RELOAD_HELPER", string.Empty).Trim();
        return confPath.Length > 0 && reloadCommand.Length > 0;
    }

    /// <summary>
    /// <see cref="RestoreRecordedAsync"/> for an undo step: a failed restore is reported as the
    /// manual-intervention error, naming the journal kept for it.
    /// </summary>
    internal static async Task<bool> RestoreRecordedOrReportAsync(
        IShellRunner shell, BlueGreenContext context, string confPath, string reloadCommand,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (await RestoreRecordedAsync(shell, context, confPath, reloadCommand, timeoutSeconds, ct).ConfigureAwait(false))
            return true;
        if (!HasRecordedUpstream(context))
        {
            await onOutput(
                $"The journal holds no record of the upstream configuration that served before this transaction "
                + $"(neither {BlueGreenJournal.UpstreamSnapshotFileName} nor {BlueGreenJournal.NoUpstreamMarkerFileName}); "
                + $"{confPath} was left untouched. Manual intervention required. Journal retained at {context.JournalDir}.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }
        await onOutput(
            $"Restoring the recorded configuration failed; manual intervention required. Journal retained at {context.JournalDir}.",
            TaskLogLevel.Error).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Puts back the configuration snapshotted when the transaction opened, reading the snapshot from
    /// the journal itself. Both undo paths need exactly this, so it lives with the rest of the
    /// upstream handling rather than in the executor that decides which undo to run.
    /// ACORE-007: <c>upstream.before</c> is put back; <c>upstream.none</c> means nothing was configured
    /// before, so the file is removed; with neither, the record is missing (a journal opened before
    /// the record existed, or damaged), which is NOT proof that there was no configuration: the live
    /// file is left untouched, nothing is reloaded, and false is returned for the caller to report.
    /// </summary>
    internal static async Task<bool> RestoreRecordedAsync(
        IShellRunner shell, BlueGreenContext context, string confPath, string reloadCommand,
        int timeoutSeconds, CancellationToken ct)
    {
        var snapshot = Path.Combine(context.JournalDir, BlueGreenJournal.UpstreamSnapshotFileName);
        string? previousConf;
        if (File.Exists(snapshot)) previousConf = await File.ReadAllTextAsync(snapshot, ct).ConfigureAwait(false);
        else if (File.Exists(Path.Combine(context.JournalDir, BlueGreenJournal.NoUpstreamMarkerFileName))) previousConf = null;
        else return false;
        return await RestoreAsync(shell, confPath, previousConf, reloadCommand, timeoutSeconds, ct).ConfigureAwait(false);
    }

    /// <summary>Whether the journal says what served before it: a snapshot, or the no-configuration marker.</summary>
    internal static bool HasRecordedUpstream(BlueGreenContext context) =>
        File.Exists(Path.Combine(context.JournalDir, BlueGreenJournal.UpstreamSnapshotFileName))
        || File.Exists(Path.Combine(context.JournalDir, BlueGreenJournal.NoUpstreamMarkerFileName));

    /// <summary>
    /// Puts back exactly the configuration that was serving and reloads, reporting whether that
    /// worked. <paramref name="previousConf"/> null means the caller KNOWS nothing was configured
    /// before (it read the file itself, or the journal holds the no-configuration marker), so the
    /// file is removed rather than left pointing at a colour nobody chose. A missing record never
    /// reaches here: <see cref="RestoreRecordedAsync"/> refuses it.
    /// </summary>
    internal static async Task<bool> RestoreAsync(
        IShellRunner shell, string confPath, string? previousConf, string reloadCommand,
        int timeoutSeconds, CancellationToken ct)
    {
        if (previousConf is not null) await WriteAtomicAsync(confPath, previousConf, ct).ConfigureAwait(false);
        else if (File.Exists(confPath)) File.Delete(confPath);

        var reload = await shell.RunExecAsync(
            "sudo", ["-n", reloadCommand], ct, TimeSpan.FromSeconds(Math.Min(120, timeoutSeconds)))
            .ConfigureAwait(false);
        return reload.ExitCode == 0;
    }
}
