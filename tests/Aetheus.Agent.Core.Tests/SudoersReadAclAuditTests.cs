// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Drift guard for the hash-derived-capability mechanism. The non-root agent hashes the sudoers
/// drop-ins in <see cref="SudoersHashCollector"/> each heartbeat; the backend derives
/// <c>PackageManagementAvailable</c>/<c>DeploymentTargetAvailable</c> from those hashes. The drop-ins
/// are <c>root:root 0440</c>, so the agent can only read them after <c>install-agent-linux.sh</c>
/// grants it a READ ACL (<c>grant_sudoers_read_acls</c>). If that grant is removed, or a hashed file
/// is added outside the <c>/etc/sudoers.d/aetheus-*</c> glob the grant loops over, EVERY hash-derived
/// capability silently freezes OFF - the exact bug that disabled package-manage/deployment on the live
/// box. There is no compile-time link between the C# list and the shell script, so this test asserts it.
/// </summary>
public sealed class SudoersReadAclAuditTests
{
    [Fact]
    public void Install_script_grants_agent_read_acl_on_hashed_dropins()
    {
        var script = ReadInstallScript();

        // 1. The read-ACL grant must exist: a setfacl read (`:r`) grant for the agent user.
        Assert.Matches(
            new Regex(@"setfacl\s+-m\s+""u:\$AGENT_USER:r""", RegexOptions.None),
            script);

        // 2. It must loop over the aetheus-* drop-in glob (so it catches every drop-in by prefix).
        Assert.Contains("/etc/sudoers.d/aetheus-*", script);

        // 3. The grant function must actually be invoked, not merely defined.
        Assert.Contains("grant_sudoers_read_acls", script);
        Assert.True(
            Regex.Matches(script, @"\bgrant_sudoers_read_acls\b").Count >= 2,
            "grant_sudoers_read_acls must be both defined AND called in install-agent-linux.sh.");
    }

    [Fact]
    public void Every_hashed_sudoers_file_is_covered_by_the_read_acl_glob()
    {
        // The install script grants the read ACL via `for f in /etc/sudoers.d/aetheus-*`. A hashed
        // file that does not match that prefix would never get the ACL → its hash never reported →
        // its capability silently OFF. Lock every KnownSudoersFiles entry to the covered prefix.
        const string coveredPrefix = "/etc/sudoers.d/aetheus-";
        foreach (var file in SudoersHashCollector.KnownSudoersFiles)
        {
            Assert.StartsWith(coveredPrefix, file, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The reverse direction, and the one whose absence cost a capability. The other test proves every
    /// HASHED file gets the ACL; nothing proved that every drop-in the installer WRITES is hashed.
    /// <c>/etc/sudoers.d/aetheus-certbot</c> fell through that gap: the installer creates it,
    /// <c>BuildEffectiveCapabilities</c> derives <c>certbot.manage</c> from
    /// <c>sudoersHashes.ContainsKey("aetheus-certbot")</c>, but the collector never read the file, so
    /// the key never existed and the capability was unreachable on every host. The symptom was silent:
    /// no diagnostic, no error, just a certificate the platform declined to issue.
    /// </summary>
    [Fact]
    public void Every_installed_sudoers_dropin_is_hashed()
    {
        var script = ReadInstallScript();

        var installed = Regex
            .Matches(script, @"SUDOERS_FILE=""(/etc/sudoers\.d/aetheus-[a-z-]+)""")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(installed);

        var hashed = SudoersHashCollector.KnownSudoersFiles.ToHashSet(StringComparer.Ordinal);
        var unhashed = installed.Where(file => !hashed.Contains(file)).ToList();

        Assert.True(
            unhashed.Count == 0,
            "install-agent-linux.sh installs sudoers drop-ins that SudoersHashCollector never reads, so "
            + "every capability derived from them is unreachable:\n" + string.Join("\n", unhashed));
    }

    private static string ReadInstallScript()
        => File.ReadAllText(Path.Combine(FindRepoRoot(), "deploy", "agent-installer", "install-agent-linux.sh"));

    private static string FindRepoRoot() => Aetheus.Agent.Core.Tests.RepositoryScan.Root;
}
