// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Recette R-8 (option B of 2026-11-07): executes a reboot order of Aetheus's reboot scheduler. The order is refused,
/// and the host left alone, when it carries no deadline or its deadline is past (an order that waited while the agent
/// was away), or when the backend does not confirm it: schedule removed, disabled or changed since the order was emitted,
/// a newer order, something else running on the server, or no answer at all. Only then does it run
/// <c>sudo -n /bin/systemctl reboot</c>, argv-exact against the <c>/etc/sudoers.d/aetheus-reboot</c> drop-in.
/// </summary>
public sealed class ServerRebootOperationExecutor(
    IShellRunner shell, IRebootOrderConfirmer confirmer, TimeProvider clock, ILogger<ServerRebootOperationExecutor> logger)
    : EnvironmentOperationExecutor
{
    internal const string TaskIdEnvVar = "AETHEUS_TASK_ID";

    /// <summary>The argv given to <c>sudo</c>; it must match the <c>aetheus-reboot</c> drop-in one-to-one.</summary>
    internal static readonly IReadOnlyList<string> RebootArgv = ["-n", "/bin/systemctl", "reboot"];

    public override bool CanHandle(OperationKind kind) => kind == OperationKind.ServerReboot;

    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await onOutput("A scheduled reboot is only supported on Linux; the host was not rebooted.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        return await RebootAsync(envVars, onOutput, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The order past the platform check: deadline, confirmation, then the reboot command.</summary>
    internal async Task<ExecutorResult> RebootAsync(
        IReadOnlyDictionary<string, string> envVars, Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        if (!envVars.TryGetValue(TaskIdEnvVar, out var taskText)
            || !int.TryParse(taskText, NumberStyles.None, CultureInfo.InvariantCulture, out var taskId))
        {
            return await RefuseAsync(RebootOrderRefusal.NotARebootOrder, "the order carries no task id", onOutput).ConfigureAwait(false);
        }

        if (!envVars.TryGetValue(ServerRebootOrder.NotAfterEnvVar, out var notAfterText)
            || !DateTime.TryParse(notAfterText, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var notAfter))
        {
            return await RefuseAsync(RebootOrderRefusal.Expired, "the order carries no deadline", onOutput).ConfigureAwait(false);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (now > notAfter)
        {
            return await RefuseAsync(RebootOrderRefusal.Expired,
                string.Create(CultureInfo.InvariantCulture, $"its deadline {notAfter:yyyy-MM-dd HH:mm} UTC is past"), onOutput)
                .ConfigureAwait(false);
        }

        var confirmation = await confirmer.ConfirmAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (confirmation is null)
        {
            return await RefuseAsync(RebootOrderRefusal.NotTheCurrentOrder, "the backend could not confirm it", onOutput).ConfigureAwait(false);
        }

        if (!confirmation.Allowed)
        {
            return await RefuseAsync(confirmation.Refusal, "the backend refused it", onOutput).ConfigureAwait(false);
        }

        await onOutput("Reboot order confirmed by Aetheus; rebooting the host now.", TaskLogLevel.Info).ConfigureAwait(false);
        logger.LogWarning("Rebooting the host for the scheduled reboot order {TaskId}", taskId);
        var result = await shell.RunExecAsync("sudo", RebootArgv, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            await onOutput(
                string.Create(CultureInfo.InvariantCulture, $"systemctl reboot failed (exit {result.ExitCode}): {result.StdErr.Trim()}"),
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(result.ExitCode, false);
        }

        return new ExecutorResult(0, false);
    }

    private async Task<ExecutorResult> RefuseAsync(
        RebootOrderRefusal refusal, string why, Func<string, TaskLogLevel, Task> onOutput)
    {
        await onOutput($"Reboot order refused ({refusal}): {why}. The host was not rebooted.", TaskLogLevel.Error).ConfigureAwait(false);
        logger.LogWarning("Reboot order refused: {Refusal}, {Why}", refusal, why);
        return new ExecutorResult(1, false, TaskFailureCodes.RebootRefused, refusal.ToString());
    }
}
