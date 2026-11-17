// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Full TeamSpeak 3 server install via the root-owned <c>teamspeak-setup</c> helper through the
/// controlled-sudo recipe - same shape as <see cref="MailOperationExecutor"/>'s setup path. The install
/// path is the validated target; the voice and query ports arrive in <c>AETHEUS_TEAMSPEAK_*</c> env
/// vars. No secret is involved (the helper generates and stores the serveradmin credential itself), so
/// nothing rides over stdin. Every field is re-validated here (last-line defence) and passed argv-only;
/// the helper re-validates too and is the security boundary.
/// </summary>
public sealed class TeamspeakSetupOperationExecutor(
    IOptions<AetheusAgentOptions> options,
    ILogger<TeamspeakSetupOperationExecutor> logger) : EnvironmentOperationExecutor
{
    private readonly AetheusAgentOptions _options = options.Value;

    // Root-owned helper deposited by install-agent-linux.sh (--module server-management /
    // --enable-teamspeak-setup). The matching /etc/sudoers.d/aetheus-teamspeak grants exactly this
    // binary, NOPASSWD. The helper re-validates every argument and is itself the security boundary.
    internal const string SetupHelperPath = "/usr/local/lib/aetheus/teamspeak-setup";

    public override bool CanHandle(OperationKind kind) => kind is OperationKind.TeamspeakSetup;

    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind,
        string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (kind != OperationKind.TeamspeakSetup)
            return new ExecutorResult(-1, false);

        envVars.TryGetValue(TeamspeakSetupEnv.VoicePort, out var voiceRaw);
        envVars.TryGetValue(TeamspeakSetupEnv.QueryPort, out var queryRaw);
        var voicePort = ParsePort(voiceRaw, 9987);
        var queryPort = ParsePort(queryRaw, 10011);

        // Validate before the OS check so a malformed request is rejected without spawning anything
        // on any platform (mirrors MailOperationExecutor / PackageOperationExecutor).
        if (!OperationTargetValidator.TeamspeakSetupInstallPathRegex().IsMatch(target) ||
            voicePort is null || queryPort is null)
        {
            logger.LogWarning("Rejected teamspeak setup with invalid parameters");
            await onOutput("Invalid teamspeak setup parameters", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            await onOutput("TeamSpeak setup is only supported on Linux", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        var psi = SudoProcessStartInfo.Create();
        foreach (var arg in BuildSetupArgv(target, voicePort.Value, queryPort.Value))
            psi.ArgumentList.Add(arg);

        return await ProcessRunner.RunAsync(psi, timeoutSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
    }

    // Full argv for `sudo` (unit-testable without spawning a process): `-n`, the helper path, the
    // validated install path, then the two ports in the exact order the helper expects.
    internal static IReadOnlyList<string> BuildSetupArgv(string installPath, int voicePort, int queryPort) =>
        ["-n", SetupHelperPath, installPath,
         voicePort.ToString(CultureInfo.InvariantCulture),
         queryPort.ToString(CultureInfo.InvariantCulture)];

    private static int? ParsePort(string? raw, int fallback)
    {
        if (string.IsNullOrEmpty(raw)) return fallback;
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535
            ? p
            : null;
    }
}
