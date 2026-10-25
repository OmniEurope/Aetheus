// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Extensions;

namespace Aetheus.Agent.Core.Workers;

public static class AgentBackendProbeEntrypoint
{
    public static Task<int?> TryRunAsync(
        string[] args,
        IConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        TryRunAsync(args, configuration, BackendProbe.RunWithTlsOptionsAsync, cancellationToken);

    internal static async Task<int?> TryRunAsync(
        string[] args,
        IConfiguration configuration,
        Func<string, string[], bool, string?, CancellationToken, Task<int>> runProbe,
        CancellationToken cancellationToken = default)
    {
        if (!HasProbeArgument(args))
            return null;

        var section = AetheusAgentOptions.SectionName;
        var serverUrl = configuration[$"{section}:ServerUrl"] ?? string.Empty;
        var allowInsecure = string.Equals(
            configuration[$"{section}:AllowInsecureCerts"], "true", StringComparison.OrdinalIgnoreCase);
        var pinnedThumbprint = configuration[$"{section}:PinnedServerCertThumbprint"];

        try
        {
            AgentTlsPosture.ValidateTlsPosture(new AetheusAgentOptions
            {
                ServerUrl = serverUrl,
                AllowInsecureCerts = allowInsecure,
                PinnedServerCertThumbprint = pinnedThumbprint
            });
        }
        catch (InvalidOperationException ex)
        {
            // The installer invokes this mode before starting the service. Surface the same TLS
            // posture rejection here so an invalid remote AllowInsecureCerts deployment fails with
            // an actionable installation error instead of becoming an unexplained offline agent.
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        return await runProbe(
            serverUrl, args, allowInsecure, pinnedThumbprint, cancellationToken).ConfigureAwait(false);
    }

    private static bool HasProbeArgument(string[] args) =>
        args.Any(argument => string.Equals(argument, "--probe-backend", StringComparison.OrdinalIgnoreCase));
}
