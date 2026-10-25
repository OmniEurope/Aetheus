// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>
/// ADR-024 4.2: reports ufw state + rules at heartbeat. Unlike apt patching, <c>ufw status</c> needs
/// root, so the read goes through the same root-owned <c>aetheus-firewall</c> helper - meaning full
/// visibility (Active + rules) requires the firewall-manage grant. No-fake decoupling: the ufw binary is
/// detected unprivileged (<c>Installed</c>), but Active/rules are only populated when the helper read
/// succeeds; a failed read reports Installed-only (never a fabricated "inactive, no rules").
/// </summary>
public sealed class FirewallCollector(
    ILogger<FirewallCollector> logger,
    IShellRunner shell,
    Func<string, bool>? fileExists = null)
    : BaseShellCollector<FirewallCollector>(logger, shell), IFirewallCollector
{
    private const string UfwPath = "/usr/sbin/ufw";
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

    public async Task<FirewallDataDto> CollectAsync(CancellationToken ct = default)
    {
        if (!_fileExists(UfwPath))
            return new FirewallDataDto(); // ufw not installed => not applicable

        try
        {
            // `ufw status` needs root; go through the helper (only works when the grant is present).
            var res = await Shell.RunExecAsync("sudo", ["-n", AgentHelperPaths.FirewallHelper, "status"], ct).ConfigureAwait(false);
            if (res.ExitCode != 0)
            {
                return new FirewallDataDto
                {
                    Installed = true,
                    StatusKnown = false,
                    CollectionDiagnostics = "ufw status could not be read; ensure the firewall-manage capability is installed"
                };
            }

            return new FirewallDataDto
            {
                Installed = true,
                Active = UfwStatusParser.IsActive(res.StdOut),
                StatusKnown = true,
                Rules = [.. UfwStatusParser.ParseRules(res.StdOut)]
            };
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "[FirewallCollector] status probe failed");
            return new FirewallDataDto
            {
                Installed = true,
                StatusKnown = false,
                CollectionDiagnostics = "ufw status probe failed; see agent logs"
            };
        }
    }
}
