// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

/// <summary>Restore and reload the exact reserve configuration without starting, stopping or
/// migrating services; the departed colour becomes the new reserve. Refuse when no reserve exists,
/// another deployment owns an open transaction or reserve readiness fails.</summary>
internal sealed class BlueGreenRevert(IShellRunner shell)
{
    /// <summary>How long the reserve gets to answer: it is supposed to be running already.</summary>
    internal static readonly TimeSpan ReadinessBudget = TimeSpan.FromSeconds(30);

    internal async Task<ExecutorResult> RevertAsync(
        BlueGreenContext context, BlueGreenJournal journal, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TimeSpan, Task<bool>> isReady,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        var reserve = new BlueGreenReserve(context);
        var confPath = envVars.GetValueOrDefault("AETHEUS_BG_UPSTREAM_CONF", string.Empty).Trim();
        var reloadCommand = envVars.GetValueOrDefault("AETHEUS_BG_RELOAD_HELPER", string.Empty).Trim();
        if (Refusal(journal, reserve, confPath, reloadCommand) is { } refusal)
        {
            await onOutput(refusal.Message, TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(refusal.ExitCode, false);
        }

        var live = journal.ReadLiveColour();
        var target = reserve.Colour!;
        if (!await isReady(target, ReadinessBudget).ConfigureAwait(false))
        {
            await onOutput(
                $"The reserve colour {target} does not answer its readiness probe; traffic was NOT moved. Redeploy the "
                + "previous release instead.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var currentConf = File.Exists(confPath) ? await File.ReadAllTextAsync(confPath, ct).ConfigureAwait(false) : null;
        if (!await BlueGreenUpstream.RestoreAsync(shell, confPath, reserve.Upstream, reloadCommand, timeoutSeconds, ct)
                .ConfigureAwait(false))
        {
            // The reload is self-validating, so the previous configuration is still what the web server
            // runs; the file is put back so disk and memory agree again.
            var restored = await BlueGreenUpstream.RestoreAsync(shell, confPath, currentConf, reloadCommand, timeoutSeconds, ct)
                .ConfigureAwait(false);
            await onOutput(
                restored
                    ? $"The web server rejected the reserve configuration; {live ?? "the live colour"} keeps serving."
                    : "The web server rejected the reserve configuration AND the restore of the live one; manual intervention required.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false);
        }

        var reserveRevision = await SwapAsync(context, journal, reserve, live, target, currentConf, ct).ConfigureAwait(false);
        await onOutput($"##aetheus[setvariable name=BLUEGREEN_REVERTED_TO]{target}", TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput($"##aetheus[setvariable name=BLUEGREEN_REVERTED_REVISION]{reserveRevision}", TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput($"##aetheus[setvariable name=BLUEGREEN_RESERVE_COLOR]{live ?? "none"}", TaskLogLevel.Info).ConfigureAwait(false);
        await onOutput(
            $"Traffic is back on {target} ({(reserveRevision.Length > 0 ? reserveRevision : "revision unknown")}); "
            + $"{live ?? "no colour"} is now kept in reserve and the same operation returns to it.",
            TaskLogLevel.Warning).ConfigureAwait(false);
        return new ExecutorResult(0, false);
    }

    private sealed record RefusalReason(string Message, int ExitCode);

    /// <summary>Why there is nothing safe to return to, or null when there is.</summary>
    private static RefusalReason? Refusal(
        BlueGreenJournal journal, BlueGreenReserve reserve, string confPath, string reloadCommand)
    {
        var state = journal.Exists ? journal.State : null;
        if (state is not (null or BlueGreenJournal.Committed))
            return new($"A deployment transaction is open (state={state}); it owns the environment. Let it finish or roll back first.", 1);
        if (!reserve.Exists || reserve.Colour == journal.ReadLiveColour())
            return new("No colour is kept in reserve (none was recorded by a commit, a later deployment recycled it, or it was "
                + "retired). Redeploy the previous release instead.", 1);
        if (confPath.Length == 0 || reloadCommand.Length == 0)
            return new("AETHEUS_BG_UPSTREAM_CONF and AETHEUS_BG_RELOAD_HELPER are required to move traffic.", -1);
        return null;
    }

    /// <summary>
    /// After the reload: the reserve becomes live with the revision it serves, and the colour traffic
    /// left becomes the reserve with the configuration and revision it had. Returns the revision now
    /// serving.
    /// </summary>
    private static async Task<string> SwapAsync(
        BlueGreenContext context, BlueGreenJournal journal, BlueGreenReserve reserve,
        string? live, string target, string? currentConf, CancellationToken ct)
    {
        var reserveRevision = reserve.Revision;
        var currentRevision = File.Exists(context.SourceCommitFile)
            ? (await File.ReadAllTextAsync(context.SourceCommitFile, ct).ConfigureAwait(false)).Trim()
            : string.Empty;
        journal.WriteLiveColour(target);
        if (reserveRevision.Length > 0) journal.WriteDeployedRevision(reserveRevision);
        if (live is not null && currentConf is not null)
            reserve.Record(live, currentRevision, currentConf);
        else
            reserve.Clear();
        return reserveRevision;
    }
}
