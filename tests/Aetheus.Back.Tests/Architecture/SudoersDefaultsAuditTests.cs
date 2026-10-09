// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// docs/contracts/security.md "Controlled Sudo Escalation": every <c>Cmnd_Alias</c> of the sudoers
/// drop-ins the installer renders carries its own <c>Defaults!&lt;alias&gt;</c> block, so no grant runs
/// with a sudo cache, the caller's environment or its PATH. The exact-argv <c>systemctl</c> grant also
/// carries <c>noexec</c> (audit DEP-001): it once had only the user-wide Defaults line.
/// </summary>
public class SudoersDefaultsAuditTests
{
    private static readonly string[] RequiredDefaults =
    [
        "timestamp_timeout=0",
        "env_reset",
        "secure_path=\"/usr/sbin:/usr/bin:/sbin:/bin\"",
    ];

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
