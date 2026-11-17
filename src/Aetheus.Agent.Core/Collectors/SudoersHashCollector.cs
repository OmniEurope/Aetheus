// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>
/// Platform-agnostic, shell-free sudoers hasher. Reads the known Aetheus sudoers
/// drop-in files via <see cref="File"/> (never a shell) and returns their SHA256
/// digests. Missing or unreadable files are silently skipped - the agent runs
/// non-root and may lack read access until an ACL grant is in place; in that case
/// the backend simply sees no hash for that file and reports no drift.
/// </summary>
public sealed class SudoersHashCollector(
    Func<string, bool>? fileExists = null,
    Func<string, CancellationToken, Task<byte[]>>? readAllBytesAsync = null) : ISudoersHashCollector
{
    // The argv-exact, noexec sudoers drop-ins installed by install-agent-linux.sh
    // under the Controlled Sudo Escalation recipe (item #6 hardening).
    // internal (not private) so SudoersReadAclAuditTests can cross-check that the install script
    // grants the non-root agent a READ ACL on each of these (else every hash-derived capability
    // silently stays OFF - the bug that froze package-manage/deployment on the live box).
    internal static readonly string[] KnownSudoersFiles =
    [
        "/etc/sudoers.d/aetheus-agent",
        "/etc/sudoers.d/aetheus-apache",
        "/etc/sudoers.d/aetheus-rkhunter",
        // Phase 3 - argv-exact drop-ins for the cron / portsentry sudo helpers and the
        // fixed-unit service-enable allow-list.
        "/etc/sudoers.d/aetheus-cron",
        "/etc/sudoers.d/aetheus-portsentry",
        "/etc/sudoers.d/aetheus-service-enable",
        // S-FEAT-W8KN - argv-exact apt-get install/purge allow-list (package-manage capability).
        "/etc/sudoers.d/aetheus-package",
        // ADR-024 4.1 - argv-exact apt-get upgrade grant (patch-manage capability). Its presence
        // is what derives Server.PatchManagementAvailable backend-side.
        "/etc/sudoers.d/aetheus-patch",
        // Recette R-8 - argv-exact systemctl reboot grant (host-reboot capability): its presence publishes
        // host.reboot, without which the backend never hands this agent a scheduled reboot order.
        "/etc/sudoers.d/aetheus-reboot",
        // ADR-024 4.2 - path-only grant for the root-owned aetheus-firewall helper (firewall-manage
        // capability). Its presence is what derives Server.FirewallManagementAvailable backend-side.
        "/etc/sudoers.d/aetheus-firewall",
        // S-FEAT-W8KN - argv-exact grant for the root-owned mail-setup helper (mail-setup capability).
        "/etc/sudoers.d/aetheus-mail",
        // argv-exact grant for the root-owned teamspeak-setup helper (teamspeak-setup capability).
        // Its presence is what derives Server.TeamspeakSetupAvailable backend-side.
        "/etc/sudoers.d/aetheus-teamspeak",
        // Deployment module - argv-exact grant for the root-owned deploy-restart helper (cross-agent
        // deploy capability). Its presence is what derives Server.DeploymentTargetAvailable backend-side.
        "/etc/sudoers.d/aetheus-deploy",
        // argv-exact grant for the root-owned certbot issue/manage helpers (certbot-manage capability).
        // Missing from this list until 2026-08-23, which made certbot.manage UNREACHABLE: the installer
        // writes the drop-in and BuildEffectiveCapabilities looks for its hash, but nothing ever hashed
        // it, so the capability could never light up and no certificate could be issued by the platform.
        // EveryInstalledSudoersDropin_IsHashed now guards the list against exactly that omission.
        "/etc/sudoers.d/aetheus-certbot",
    ];

    public async Task<Dictionary<string, string>> CollectAsync(CancellationToken ct = default)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in KnownSudoersFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!(fileExists ?? File.Exists)(path)) continue;
                var bytes = await (readAllBytesAsync ?? File.ReadAllBytesAsync)(path, ct).ConfigureAwait(false);
                var digest = SHA256.HashData(bytes);
                hashes[Path.GetFileName(path)] = Convert.ToHexString(digest);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Permission denied / IO error - skip this file. Drift detection
                // degrades gracefully to "no signal" rather than a false alarm.
            }
        }

        return hashes;
    }
}
