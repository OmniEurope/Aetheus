// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

/// <summary>Run typed mail operations through root-owned mail-setup or mail-manage using the exact
/// sudoers grant. MailManageCommandBuilder validates argv, while secrets and messages use
/// stdin.</summary>
public sealed class MailOperationExecutor(
    IOptions<AetheusAgentOptions> options,
    ILogger<MailOperationExecutor> logger) : IOperationExecutor
{
    private static readonly IReadOnlyDictionary<string, string> s_noEnv = new Dictionary<string, string>();

    private readonly AetheusAgentOptions _options = options.Value;

    // Kept here (as well as on the builder) because MailHelperPathAuditTests pins both paths against the
    // install script's MAIL_SETUP_HELPER_PATH / MAIL_MANAGE_HELPER_PATH.
    internal const string SetupHelperPath = MailManageCommandBuilder.SetupHelperPath;
    internal const string ManageHelperPath = MailManageCommandBuilder.ManageHelperPath;

    public bool CanHandle(OperationKind kind) =>
        kind is >= OperationKind.MailStartPostfix and <= OperationKind.MailPurge
            or OperationKind.MailMasterAccess or OperationKind.MailSetQuota or OperationKind.MailRetryTuning;

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken) =>
        ExecuteAsync(kind, target, s_noEnv, timeoutSeconds, onOutput, cancellationToken);

    public async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        // Validate before the OS check so a malformed request is rejected without spawning anything on any
        // platform (mirrors PackageOperationExecutor).
        var command = MailManageCommandBuilder.Build(kind, target, envVars);
        if (command is null)
        {
            logger.LogWarning("Rejected mail {Kind} with invalid parameters", kind);
            var message = kind == OperationKind.MailSetup ? "Invalid mail setup parameters" : "Invalid mail operation parameters";
            await onOutput(message, TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var platformFailure = await OperationPlatformGuard.RequireLinuxAsync(
            onOutput, "Mail operations are only supported on Linux").ConfigureAwait(false);
        if (platformFailure is not null) return platformFailure;

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        var psi = SudoProcessStartInfo.Create();
        foreach (var arg in command.Argv)
            psi.ArgumentList.Add(arg);

        return await ProcessRunner.RunAsync(
            psi, timeoutSeconds, onOutput, logger, cancellationToken, standardInput: command.Stdin).ConfigureAwait(false);
    }
}
