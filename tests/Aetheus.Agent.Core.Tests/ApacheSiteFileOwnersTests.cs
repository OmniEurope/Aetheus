// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Recette R-14: the agent reports the account that owns a site file only when it is not its own (the sticky
/// sites directories then refuse the deletion); an owner it cannot read is never guessed.</summary>
public sealed class ApacheSiteFileOwnersTests
{
    private const string Available = "/etc/apache2/sites-available";
    private const string Enabled = "/etc/apache2/sites-enabled";

    private static ApacheVirtualHostDto Host(string file, bool enabled) => new()
    {
        ServerName = "api.example.test",
        Port = 80,
        IsEnabled = enabled,
        ConfigFile = enabled ? $"{Enabled}/{file}" : $"{Available}/{file}"
    };

    [Fact]
    public void Parse_ReadsOwnerAndPath_AndIgnoresAnythingElse()
    {
        var owners = ApacheSiteFileOwners.Parse(
            "root /etc/apache2/sites-available/a.conf\n"
            + "aetheus /etc/apache2/sites-available/b.conf\n"
            + "UNKNOWN /etc/apache2/sites-available/c.conf\n"
            + "stat: cannot statx '/etc/apache2/sites-available/d.conf': No such file or directory\n"
            + "Server version: Apache/2.4.58\n");

        Assert.Equal(2, owners.Count);
        Assert.Equal("root", owners["/etc/apache2/sites-available/a.conf"]);
        Assert.Equal("aetheus", owners["/etc/apache2/sites-available/b.conf"]);
    }

    [Fact]
    public void PathsToRead_NamesTheAvailableFile_AndTheEnabledLinkOfAnEnabledSite_Once()
    {
        var paths = ApacheSiteFileOwners.PathsToRead(
            [Host("a.conf", enabled: true), Host("a.conf", enabled: true), Host("b.conf", enabled: false)], Available, Enabled);

        Assert.Equal([$"{Available}/a.conf", $"{Enabled}/a.conf", $"{Available}/b.conf"], paths);
    }

    [Fact]
    public void Apply_ReportsOnlyAnOwnerOtherThanTheAgent()
    {
        var owners = new Dictionary<string, string>
        {
            [$"{Available}/root.conf"] = "root",
            [$"{Available}/mine.conf"] = "aetheus",
            [$"{Available}/certbot.conf"] = "certbot"
        };

        var result = ApacheSiteFileOwners.Apply(
            [Host("root.conf", enabled: false), Host("mine.conf", enabled: false), Host("certbot.conf", enabled: false), Host("unknown.conf", enabled: false)],
            owners, "aetheus", Available, Enabled);

        Assert.Equal(["root", "", "certbot", ""], result.Select(host => host.ConfigFileForeignOwner));
    }

    [Fact]
    public void Apply_ForAnEnabledSite_AlsoReadsTheOwnerOfItsLink()
    {
        var owners = new Dictionary<string, string>
        {
            [$"{Available}/a.conf"] = "aetheus",
            [$"{Enabled}/a.conf"] = "root"
        };

        var result = ApacheSiteFileOwners.Apply([Host("a.conf", enabled: true)], owners, "aetheus", Available, Enabled);

        Assert.Equal("root", Assert.Single(result).ConfigFileForeignOwner);
    }

    [Fact]
    public async Task TheCollector_ReportsTheOwnerOfADisabledFileItCannotDelete_AndNoneWhenStatFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "aetheus-owners-" + Guid.NewGuid().ToString("N"));
        var available = Directory.CreateDirectory(Path.Combine(root, "sites-available")).FullName.Replace('\\', '/');
        var enabled = Directory.CreateDirectory(Path.Combine(root, "sites-enabled")).FullName.Replace('\\', '/');
        try
        {
            File.WriteAllText(Path.Combine(available, "old.conf"), "<VirtualHost *:80>\n ServerName old.example.test\n</VirtualHost>\n");
            var shell = Substitute.For<IShellRunner>();
            shell.RunExecAsync("which", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ShellExecResult(0, "/usr/sbin/apache2\n", string.Empty));
            shell.RunExecAsync(Arg.Is<string>(name => name != "which" && name != "stat"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ShellExecResult(0, string.Empty, string.Empty));
            shell.RunExecAsync("stat", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ShellExecResult(0, $"root {available}/old.conf\n", string.Empty));
            ApacheCollector Build() => new(NullLogger<ApacheCollector>.Instance, shell)
            {
                IsWindows = false,
                DirectoryExists = Directory.Exists,
                SitesAvailableDirectory = available,
                SitesEnabledDirectory = enabled,
                ReadLinkTarget = _ => null,
                AgentUser = () => "aetheus"
            };

            var reported = await Build().CollectAsync(TestContext.Current.CancellationToken);
            Assert.Equal("root", Assert.Single(reported.VirtualHosts, host => !host.IsEnabled).ConfigFileForeignOwner);

            shell.RunExecAsync("stat", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(new ShellExecResult(1, string.Empty, "stat: cannot statx"));
            var unknown = await Build().CollectAsync(TestContext.Current.CancellationToken);
            Assert.Equal(string.Empty, Assert.Single(unknown.VirtualHosts, host => !host.IsEnabled).ConfigFileForeignOwner);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
