// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// type: certbot - obtains/installs an HTTPS certificate on the host. certbot is a GTFOBins
/// root-escalation primitive and its argv is domain-variable, so it can NOT be granted via the
/// argv-exact controlled-sudo recipe directly. Instead the privileged logic lives in a root-owned
/// helper <c>/usr/local/lib/aetheus/aetheus-certbot-issue</c> (deposited by
/// <c>install-agent-linux.sh --enable-certbot-manage</c>, same pattern as the mail/deploy helpers):
/// the agent invokes it argv-exact via sudo with the domains/email in <c>AETHEUS_CERTBOT_*</c> env
/// vars; the helper re-validates them and performs real ACME issuance through a stable Apache
/// webroot without stopping the server. Production ACME failures fail honestly; the explicit local
/// mode never contacts ACME and creates a self-signed certificate in the Let's Encrypt layout so the
/// disposable local simulator still exercises HTTPS.
///
/// If the helper is not installed, sudo fails and the step fails honestly (never a fake green).
/// </summary>
public sealed class CertbotOperationExecutor(
    IOptions<AetheusAgentOptions> options,
    ILogger<CertbotOperationExecutor> logger) : IOperationExecutor
{
    private const string HelperPath = "/usr/local/lib/aetheus/aetheus-certbot-issue";
    private const string ManageHelperPath = "/usr/local/lib/aetheus/aetheus-certbot-manage";
    private readonly AetheusAgentOptions _options = options.Value;

    internal bool IsLinux { get; init; } = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    // The process runner, replaceable so the helper argv and environment are asserted without sudo.
    internal Func<ProcessStartInfo, int, Func<string, TaskLogLevel, Task>, ILogger, CancellationToken, Task<ExecutorResult>>
        RunProcessAsync
    { get; init; } = static (startInfo, timeoutSeconds, onOutput, processLogger, ct) =>
            ProcessRunner.RunAsync(startInfo, timeoutSeconds, onOutput, processLogger, ct);

    public bool CanHandle(OperationKind kind) => kind is OperationKind.CertbotObtain
        or OperationKind.CertbotRenew or OperationKind.CertbotRenewAll
        or OperationKind.CertbotDelete or OperationKind.CertbotRevoke
        or OperationKind.CertbotNormalize or OperationKind.CertbotRenewalCheck;

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
        => kind is OperationKind.CertbotObtain
            ? ObtainAsync(target, envVars, timeoutSeconds, onOutput, cancellationToken)
            : ManageAsync(kind, target, timeoutSeconds, onOutput, cancellationToken);

    public Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
        => kind is OperationKind.CertbotObtain
            ? ObtainAsync(target, new Dictionary<string, string>(), timeoutSeconds, onOutput, cancellationToken)
            : ManageAsync(kind, target, timeoutSeconds, onOutput, cancellationToken);

    private async Task<ExecutorResult> ObtainAsync(
        string primaryDomain,
        IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (!IsLinux)
        {
            await onOutput("Certbot operations are only supported on Linux", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        if (!OperationTargetValidator.IsValid(OperationKind.CertbotObtain, primaryDomain))
        {
            await onOutput($"Invalid certbot target domain '{primaryDomain}'.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        var psi = SudoProcessStartInfo.Create();
        foreach (var arg in BuildObtainArgv(primaryDomain)) psi.ArgumentList.Add(arg);

        // Domains/email travel in env (kept off the argv / process list); the helper re-validates them.
        psi.Environment["AETHEUS_CERTBOT_DOMAINS"] = envVars.GetValueOrDefault("AETHEUS_CERTBOT_DOMAINS", primaryDomain);
        psi.Environment["AETHEUS_CERTBOT_EMAIL"] = envVars.GetValueOrDefault("AETHEUS_CERTBOT_EMAIL", string.Empty);
        var mode = envVars.GetValueOrDefault("AETHEUS_CERTBOT_MODE", "production");
        if (mode is not ("production" or "local"))
        {
            await onOutput($"Invalid certificate mode '{mode}'.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }
        psi.Environment["AETHEUS_CERTBOT_MODE"] = mode;

        await onOutput(mode == "local"
            ? $"Creating local certificate for {primaryDomain} (ACME disabled)."
            : $"Requesting certificate for {primaryDomain} (real ACME via Apache webroot).", TaskLogLevel.Info).ConfigureAwait(false);
        return await RunProcessAsync(psi, timeoutSeconds, onOutput, logger, ct).ConfigureAwait(false);
    }

    // Renew / delete / revoke a certificate by lineage name (or renew all) via the root-owned
    // aetheus-certbot-manage helper, invoked argv-exact through the same controlled-sudo grant as the
    // issue helper. certbot stays GTFOBins-forbidden as a free-form sudo target; the helper IS the
    // boundary (re-validates the name, runs the fixed certbot verb). If the helper is not installed, sudo
    // fails and the step fails honestly.
    private async Task<ExecutorResult> ManageAsync(
        OperationKind kind, string certName, int timeoutSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (!IsLinux)
        {
            await onOutput("Certbot operations are only supported on Linux", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        if (!OperationTargetValidator.IsValid(kind, certName))
        {
            await onOutput($"Invalid certbot certificate name '{certName}'.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        var verb = kind switch
        {
            OperationKind.CertbotRenew => "renew",
            OperationKind.CertbotRenewAll => "renew-all",
            OperationKind.CertbotDelete => "delete",
            OperationKind.CertbotRevoke => "revoke",
            OperationKind.CertbotNormalize => "normalize",
            OperationKind.CertbotRenewalCheck => "renewal-check",
            _ => string.Empty
        };
        if (verb.Length == 0)
        {
            await onOutput($"Unsupported certbot operation '{kind}'.", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(-1, false);
        }

        timeoutSeconds = Math.Clamp(timeoutSeconds, _options.MinTimeoutSeconds, _options.MaxTimeoutSeconds);

        var psi = SudoProcessStartInfo.Create();
        foreach (var arg in BuildManageArgv(kind, verb, certName)) psi.ArgumentList.Add(arg);

        await onOutput($"certbot {verb}{(IsLineageless(kind) ? " (all)" : $" {certName}")}…", TaskLogLevel.Info).ConfigureAwait(false);
        return await RunProcessAsync(psi, timeoutSeconds, onOutput, logger, ct).ConfigureAwait(false);
    }

    // `sudo -n <issue-helper> <domain>` - argv-exact; the helper re-validates the domain.
    internal static string[] BuildObtainArgv(string primaryDomain) =>
        ["-n", HelperPath, primaryDomain];

    // `sudo -n <manage-helper> <verb> [certName]` - argv-exact; the all-lineage verbs take no name.
    internal static string[] BuildManageArgv(OperationKind kind, string verb, string certName) =>
        IsLineageless(kind)
            ? ["-n", ManageHelperPath, verb]
            : ["-n", ManageHelperPath, verb, certName];

    private static bool IsLineageless(OperationKind kind) =>
        kind is OperationKind.CertbotRenewAll or OperationKind.CertbotNormalize or OperationKind.CertbotRenewalCheck;
}
