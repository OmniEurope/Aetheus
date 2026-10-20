// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Phase 3: typed cron save/delete. The agent never writes <c>/etc/cron.d</c> itself (that would be a
/// standing root-equivalent capability); instead it invokes the root-owned <c>aetheus-cron-apply</c>
/// helper through an argv-exact sudoers grant. The helper re-validates every field and can ONLY write
/// <c>/etc/cron.d/aetheus-&lt;id&gt;</c>. All caller input is re-validated here as a last-line defence
/// and passed argv-only (no shell, no interpolation) so a metacharacter can never reach a shell.
/// Linux only - cron management is not supported on Windows.
/// </summary>
public sealed class CronOperationExecutor(
    IOptions<AetheusAgentOptions> options,
    ILogger<CronOperationExecutor> logger) : IOperationExecutor
{
    private readonly AetheusAgentOptions _options = options.Value;

    internal bool IsLinux { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    // The process runner, replaceable so the helper argv is asserted without sudo.
    internal Func<ProcessStartInfo, int, Func<string, TaskLogLevel, Task>, ILogger, CancellationToken, Task<ExecutorResult>>
        RunProcessAsync
    { get; init; } = static (startInfo, timeoutSeconds, onOutput, processLogger, ct) =>
            ProcessRunner.RunAsync(startInfo, timeoutSeconds, onOutput, processLogger, ct);

    // Root-owned helper deposited by install-agent-linux.sh (--module server-management). The
    // matching /etc/sudoers.d/aetheus-cron grants exactly this binary, NOPASSWD.
    internal const string HelperPath = "/usr/local/lib/aetheus/cron-apply";

    public bool CanHandle(OperationKind kind) => kind is OperationKind.CronSave or OperationKind.CronDelete;

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
        => RunAsync(kind, target, envVars, timeoutSeconds, onOutput, cancellationToken);

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
        => RunAsync(kind, target, new Dictionary<string, string>(), timeoutSeconds, onOutput, cancellationToken);

    private async Task<ExecutorResult> RunAsync(
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (!IsLinux)
        {
            await onOutput("Cron operations are only supported on Linux", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        // Last-line defence: the job id is the cron.d filename suffix - reject anything malformed.
        if (!CronValidation.IsValidIdentifier(target))
        {
            logger.LogWarning("Rejected cron job id with invalid format");
            await onOutput("Invalid cron job id format", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        var psi = SudoProcessStartInfo.Create();
        psi.ArgumentList.Add("-n"); // never prompt - NOPASSWD is required by the sudoers rule
        psi.ArgumentList.Add(HelperPath);

        if (kind == OperationKind.CronDelete)
        {
            psi.ArgumentList.Add("delete");
            psi.ArgumentList.Add(target);
            return await RunProcessAsync(psi, timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
        }

        // CronSave: user/schedule/command travel in env vars; re-validate each before the helper.
        envVars.TryGetValue("AETHEUS_CRON_USER", out var user);
        envVars.TryGetValue("AETHEUS_CRON_SCHEDULE", out var schedule);
        envVars.TryGetValue("AETHEUS_CRON_COMMAND", out var command);

        // IsValidNonRootUser (not IsValidUser): a root cron job is the agent-compromise→root escalation
        // path closed in lockstep with the backend. IsValidScheduleSyntax now also enforces the 5-field
        // cron.d format, rejecting a 6-field (seconds) schedule that the helper would mis-write.
        if (!CronValidation.IsValidNonRootUser(user) ||
            !CronValidation.IsValidScheduleSyntax(schedule) ||
            !CronValidation.IsValidCommand(command))
        {
            logger.LogWarning("Rejected cron save with invalid user/schedule/command");
            await onOutput("Invalid cron user, schedule or command", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        psi.ArgumentList.Add("save");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add(user!);
        psi.ArgumentList.Add(schedule!);
        psi.ArgumentList.Add(command!);

        return await RunProcessAsync(psi, timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
    }
}
