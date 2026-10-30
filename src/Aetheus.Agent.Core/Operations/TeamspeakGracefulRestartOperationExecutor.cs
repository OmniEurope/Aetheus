// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Composite TeamSpeak maintenance ops that the old shell path could not run (the agent's CommandValidator
/// rejected the <c>printf|nc &amp;&amp; sleep &amp;&amp; ... &amp;&amp; systemctl restart</c> chain and the
/// <c>tail | sort | tail</c> log pipe):
/// <list type="bullet">
/// <item><b>Graceful restart</b> - broadcast a warning (gm) over the native ServerQuery TCP client, wait
/// the grace period, kick everyone, then restart the installed TeamSpeak systemd unit (<see cref="TeamspeakUnit"/>)
/// via the aetheus-teamspeak sudoers grant. Each phase reports honestly; a failed restart is a non-zero exit (never a fake green).</item>
/// <item><b>Log read</b> - tail the newest file under <c>{installPath}/logs/</c> by direct file read
/// (read access via the teamspeak group), no shell pipe.</item>
/// </list>
/// </summary>
public sealed class TeamspeakGracefulRestartOperationExecutor(
    ITeamspeakQueryClient queryClient,
    IOptions<AetheusAgentOptions> options,
    ILogger<TeamspeakGracefulRestartOperationExecutor> logger) : EnvironmentOperationExecutor
{
    private readonly AetheusAgentOptions _options = options.Value;

    internal bool IsLinux { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    internal string CredentialPath { get; init; } = ServerQueryHelper.ReadCredentialPath();

    // The grace-period wait, replaceable so the sequence is asserted without waiting.
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } =
        static (delay, ct) => Task.Delay(delay, ct);

    // Replaceable so the unit choice is asserted without systemd (R-119).
    internal Func<string, CancellationToken, Task<bool>>? UnitExistsAsync { get; init; }

    // The process runner, replaceable so the restart argv is asserted without sudo.
    internal Func<ProcessStartInfo, int, Func<string, TaskLogLevel, Task>, ILogger, CancellationToken, Task<ExecutorResult>>
        RunProcessAsync
    { get; init; } = static (startInfo, timeoutSeconds, onOutput, processLogger, ct) =>
            ProcessRunner.RunAsync(startInfo, timeoutSeconds, onOutput, processLogger, ct);

    public override bool CanHandle(OperationKind kind) =>
        kind is OperationKind.TeamspeakGracefulRestart or OperationKind.TeamspeakGetLogs;

    public override Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
        => kind == OperationKind.TeamspeakGetLogs
            ? GetLogsAsync(target, onOutput)
            : GracefulRestartAsync(envVars, timeoutSeconds, onOutput, cancellationToken);

    private async Task<ExecutorResult> GracefulRestartAsync(
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (await RejectNonLinuxAsync(onOutput).ConfigureAwait(false))
            return new ExecutorResult(-1, false);

        var queryPort = await ServerQueryHelper.GetQueryPortAsync(envVars, onOutput).ConfigureAwait(false);
        if (queryPort is null) return new ExecutorResult(-1, false);
        var port = queryPort.Value;

        var seconds = 0;
        if (envVars.TryGetValue(TeamspeakSetupEnv.WarnSeconds, out var secRaw))
            int.TryParse(secRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
        seconds = Math.Clamp(seconds, 0, 600);
        var message = envVars.GetValueOrDefault(TeamspeakSetupEnv.WarnMessage, "Server restarting");

        var credential = await ServerQueryHelper.ReadCredentialAsync(CredentialPath, onOutput, ct).ConfigureAwait(false);
        if (credential is null) return new ExecutorResult(-1, false);

        // 1) Broadcast the warning, 2) wait the grace period, 3) kick everyone. Each ServerQuery runs
        //    over the native TCP client (no shell, no nc). A failed gm/kick is logged but does not abort
        //    the restart - the operator asked for a restart.
        var escCred = ServerQueryHelper.EscapeServerQuery(credential);
        var gm = $"login serveradmin {escCred}\nuse sid=1\ngm msg={ServerQueryHelper.EscapeServerQuery(message)}\nquit\n";
        await onOutput($"Broadcasting restart warning (grace {seconds}s)…", TaskLogLevel.Info).ConfigureAwait(false);
        await queryClient.ExecuteAsync(port, gm, ct).ConfigureAwait(false);

        if (seconds > 0)
            await DelayAsync(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);

        var kick = $"login serveradmin {escCred}\nuse sid=1\nclientkick reasonid=5 reasonmsg=Restart cldbid=0\nquit\n";
        await onOutput("Kicking connected clients…", TaskLogLevel.Info).ConfigureAwait(false);
        await queryClient.ExecuteAsync(port, kick, ct).ConfigureAwait(false);

        // 4) Restart the systemd unit. R-119: the installed TeamSpeak unit (TeamspeakUnit), through the argv-exact
        //    aetheus-teamspeak grant; the former `ts3server` name matched no install. Without the grant sudo fails
        //    and the step fails honestly.
        var unit = await TeamspeakUnit.ResolveAsync(
            UnitExistsAsync ?? ((name, token) => TeamspeakUnit.ExistsAsync(name, logger, token)), ct).ConfigureAwait(false);
        if (unit is null)
        {
            await onOutput(TeamspeakUnit.NoUnitMessage, TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        var restartTimeout = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);
        var psi = SudoProcessStartInfo.Create();
        foreach (var argument in TeamspeakUnit.BuildSystemctlArgv("restart", unit)) psi.ArgumentList.Add(argument);

        await onOutput($"Restarting {unit}…", TaskLogLevel.Info).ConfigureAwait(false);
        return await RunProcessAsync(psi, restartTimeout, onOutput, logger, ct).ConfigureAwait(false);
    }

    private async Task<bool> RejectNonLinuxAsync(
        Func<string, TaskLogLevel, Task> onOutput)
    {
        if (IsLinux) return false;

        await onOutput(
            "TeamSpeak operations are only supported on Linux",
            TaskLogLevel.Error).ConfigureAwait(false);
        return true;
    }

    private async Task<ExecutorResult> GetLogsAsync(
        string installPath,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        var logsDir = Path.Combine(installPath, "logs");
        if (!Directory.Exists(logsDir))
        {
            await onOutput($"No TeamSpeak logs directory at {logsDir}", TaskLogLevel.Warning).ConfigureAwait(false);
            return new ExecutorResult(0, false); // nothing to show - honest empty success, not a failure
        }

        string? newest = null;
        var newestTime = DateTime.MinValue;
        try
        {
            foreach (var file in Directory.EnumerateFiles(logsDir, "*.log"))
            {
                var t = File.GetLastWriteTimeUtc(file);
                if (t >= newestTime) { newestTime = t; newest = file; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await onOutput($"Could not enumerate {logsDir}: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        if (newest is null)
        {
            await onOutput($"No .log files under {logsDir}", TaskLogLevel.Warning).ConfigureAwait(false);
            return new ExecutorResult(0, false);
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(newest).ConfigureAwait(false);
            var tail = lines.Length <= 100 ? lines : lines[^100..];
            await onOutput(string.Join('\n', tail), TaskLogLevel.Info).ConfigureAwait(false);
            return new ExecutorResult(0, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await onOutput($"Could not read {newest}: {ex.Message}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
    }
}
