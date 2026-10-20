// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Every sudoers drop-in the agent installer renders (ADR-024 4.1 and 4.2, docs/contracts/security.md
/// "Controlled Sudo Escalation"), in one parameterized guard that replaces the former firewall, patch,
/// manageable-packages and defaults guards, which each re-implemented the same
/// "isolate the Cmnd_Alias block" logic. For each drop-in: the installer renders it, its
/// <c>Cmnd_Alias</c> block grants exactly what the agent sends, every alias carries its own
/// <c>Defaults!</c> block, and the opt-in capabilities stay behind their flag. A drift in a binary
/// path, verb or flag would make sudo reject the call at runtime, a capability dead on arrival.
/// </summary>
public class SudoersAuditTests
{
    private const string Firewall = "firewall";
    private const string Patch = "patch";
    private const string Package = "package";

    private static readonly string[] RequiredDefaults =
    [
        "timestamp_timeout=0",
        "env_reset",
        "secure_path=\"/usr/sbin:/usr/bin:/sbin:/bin\"",
    ];

    /// <summary>The drop-in template, the installer line that renders it, and its sudoers alias.</summary>
    private static (string Template, string RenderLine, string Alias) DropInOf(string capability) => capability switch
    {
        Firewall => ("firewall/sudoers.d/aetheus-firewall",
            "render_host_config firewall/sudoers.d/aetheus-firewall \"$FIREWALL_MANAGE_SUDOERS_FILE\"",
            "AETHEUS_FIREWALL"),
        Patch => ("patch/sudoers.d/aetheus-patch",
            "render_host_config patch/sudoers.d/aetheus-patch \"$PATCH_MANAGE_SUDOERS_FILE\"",
            "AETHEUS_PATCH"),
        Package => ("package/sudoers.d/aetheus-package",
            "render_host_config package/sudoers.d/aetheus-package \"$PACKAGE_MANAGE_SUDOERS_FILE\"",
            "AETHEUS_PACKAGE"),
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unknown sudoers capability."),
    };

    public static TheoryData<string> Capabilities() => new() { Firewall, Patch, Package };

    public static TheoryData<string, string, string> OptInCapabilities() => new()
    {
        { Firewall, "--enable-firewall-manage", "write_firewall_manage" },
        { Patch, "--enable-patch-manage", "write_patch_manage_sudoers" },
    };

    public static TheoryData<string> DropIns()
    {
        var root = Path.Combine(RepositoryScan.Root, "deploy", "agent-host-config");
        var data = new TheoryData<string>();
        foreach (var file in RepositoryScan.Enumerate(root, "*")
                     .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == "sudoers.d")
                     .Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }
        return data;
    }

    private static string Script() => LinuxHostConfigTemplates.Installer();

    /// <summary>The <c>Cmnd_Alias</c> block of a drop-in, up to its <c>Defaults!alias</c> terminator.</summary>
    private static string AliasBlock(string capability)
    {
        var (template, renderLine, alias) = DropInOf(capability);
        // The drop-in is the template the installer renders.
        Assert.Contains(renderLine, Script(), StringComparison.Ordinal);
        var dropIn = LinuxHostConfigTemplates.Read(template);
        var start = dropIn.IndexOf($"Cmnd_Alias {alias}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Cmnd_Alias {alias} not found in {template}");
        var end = dropIn.IndexOf($"Defaults!{alias}", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Defaults!{alias} terminator not found");
        return dropIn[start..end];
    }

    // The leading `-n` is a sudo flag, not part of the sudoers Cmnd match.
    private static string Cmnd(IEnumerable<string> argv) => string.Join(' ', argv.Where(a => a != "-n"));

    [Theory]
    [MemberData(nameof(Capabilities))]
    public void DropIn_IsRenderedByTheInstaller_AndHasItsAliasBlock(string capability) =>
        Assert.NotEmpty(AliasBlock(capability));

    [Theory]
    [MemberData(nameof(Capabilities))]
    public void DropIn_GrantsExactlyWhatTheAgentSends(string capability)
    {
        var block = AliasBlock(capability);
        switch (capability)
        {
            case Firewall:
                // The grant references the same helper path the agent execs (via the FIREWALL_HELPER_PATH placeholder).
                Assert.Contains("#{FIREWALL_HELPER_PATH}#", block, StringComparison.Ordinal);
                Assert.Contains(
                    "FIREWALL_HELPER_PATH=\"" + FirewallOperationExecutor.HelperPath + "\"",
                    Script(),
                    StringComparison.Ordinal);
                break;

            case Patch:
                Assert.Contains(
                    Cmnd(SystemPackageUpgradeExecutor.BuildAptUpgradeArgv()),
                    block,
                    StringComparison.Ordinal);
                break;

            case Package:
                // Match full sudo command forms, not just package names, against the agent argument builders.
                Assert.Contains(Cmnd(PackageOperationExecutor.BuildAptUpdateArgv()), block, StringComparison.Ordinal);

                // Every installable package's exact install argv must appear verbatim.
                foreach (var pkg in ManageablePackages.All)
                    Assert.Contains(
                        Cmnd(PackageOperationExecutor.BuildAptArgv(PackageOperationExecutor.InstallVerb, pkg)),
                        block,
                        StringComparison.Ordinal);

                // Every removable package's exact uninstall argv (the verb the executor really sends) must appear.
                Assert.Equal("purge", PackageOperationExecutor.UninstallVerb);
                foreach (var pkg in ManageablePackages.Removable)
                    Assert.Contains(
                        Cmnd(PackageOperationExecutor.BuildAptArgv(PackageOperationExecutor.UninstallVerb, pkg)),
                        block,
                        StringComparison.Ordinal);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unknown sudoers capability.");
        }
    }

    [Theory]
    [MemberData(nameof(OptInCapabilities))]
    public void Capability_IsOptIn_BehindItsOwnFlag(string capability, string flag, string writer)
    {
        Assert.NotEmpty(capability);
        var script = Script();
        Assert.Contains(flag, script, StringComparison.Ordinal);
        Assert.Contains(writer, script, StringComparison.Ordinal);
    }

    [Fact]
    public void Firewall_HelperIsRendered_WithAntiLockout()
    {
        var script = Script();
        Assert.Contains("render_host_config firewall/aetheus-firewall \"$FIREWALL_HELPER_PATH\"", script, StringComparison.Ordinal);
        // Anti-lockout logic must live in the helper: never close the admin port; auto-allow before enable.
        Assert.Contains(
            "would close the administration port",
            LinuxHostConfigTemplates.Read("firewall/aetheus-firewall"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void PackageDropIn_MatchesManageablePackages_All_And_Removable()
    {
        var block = AliasBlock(Package);

        var installs = Regex.Matches(block, @"apt-get install -y ([^,\s\\]+)")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var purges = Regex.Matches(block, @"apt-get purge -y ([^,\s\\]+)")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            ManageablePackages.All.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            installs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            ManageablePackages.Removable.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            purges.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        // Uninstall grants use purge rather than remove.
        Assert.DoesNotMatch(@"apt-get remove\b", block);

        Assert.Matches(@"apt-get update", block);
    }

    [Theory]
    [MemberData(nameof(DropIns))]
    public void Every_command_alias_has_its_defaults(string dropIn)
    {
        var text = LinuxHostConfigTemplates.Read(dropIn);
        var aliases = Regex.Matches(text, @"^Cmnd_Alias\s+(\w+)", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToList();
        Assert.NotEmpty(aliases);
        foreach (var alias in aliases)
        {
            foreach (var setting in RequiredDefaults)
            {
                Assert.Contains($"Defaults!{alias} {setting}\n", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_systemctl_grant_runs_without_child_processes()
    {
        var text = LinuxHostConfigTemplates.Read("agent/sudoers.d/aetheus-agent");
        var alias = text.IndexOf("Cmnd_Alias AETHEUS_SYSTEMCTL", StringComparison.Ordinal);
        var noexec = text.IndexOf("Defaults!AETHEUS_SYSTEMCTL noexec\n", StringComparison.Ordinal);
        var grant = text.IndexOf("NOPASSWD: AETHEUS_SYSTEMCTL", StringComparison.Ordinal);
        Assert.True(alias >= 0, "Cmnd_Alias AETHEUS_SYSTEMCTL not found");
        // sudoers resolves an alias only once it is defined: the Defaults line sits between the two.
        Assert.True(noexec > alias && noexec < grant, "Defaults!AETHEUS_SYSTEMCTL noexec must follow the alias and precede the grant");
    }
}
