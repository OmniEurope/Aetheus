// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Recette R-12: a site disabled from the Apache page stays listed (disabled) so it can be deleted;
/// <c>apache2ctl -S</c> alone lists only the loaded sites.</summary>
public sealed class ApacheDisabledSitesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aetheus-disabled-sites-" + Guid.NewGuid().ToString("N"));
    private readonly string _available;
    private readonly string _enabled;

    // Windows test hosts cannot create symbolic links: a stand-in link file holds "LINK->" and its target.
    private static string? ReadStandInLink(string path) =>
        File.ReadAllText(path) is var text && text.StartsWith("LINK->", StringComparison.Ordinal) ? text["LINK->".Length..] : null;

    public ApacheDisabledSitesTests()
    {
        _available = Directory.CreateDirectory(Path.Combine(_root, "sites-available")).FullName;
        _enabled = Directory.CreateDirectory(Path.Combine(_root, "sites-enabled")).FullName;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Available(string name, string content) => File.WriteAllText(Path.Combine(_available, name), content);

    private void EnabledLink(string name, string target) => File.WriteAllText(Path.Combine(_enabled, name), "LINK->" + target);

    [Fact]
    public void OnlyTheFilesNoEnabledEntryNamesOrLinksTo_AreReported_OnePerVirtualHostBlock()
    {
        Available("loaded.conf", "<VirtualHost *:80>\n  ServerName loaded.example.com\n</VirtualHost>\n");
        Available("linked.conf", "<VirtualHost *:80>\n  ServerName linked.example.com\n</VirtualHost>\n");
        Available("aetheus-api.example.com.conf", """
            # Managed by Aetheus
            <VirtualHost *:80>
                # ServerName commented.example.com
                ServerName aetheus-api.example.com
                RewriteEngine On
            </VirtualHost>
            """);
        Available("aetheus-api.example.com-ssl.conf", """
            <IfModule mod_ssl.c>
            <VirtualHost *:443>
                ServerName aetheus-api.example.com
                DocumentRoot "/var/www/api"
            </VirtualHost>
            <virtualhost 10.0.0.1:8443 [::1]:8443>
                ServerName alt.example.com
            </virtualhost>
            </IfModule>
            """);
        Available("notes.txt", "<VirtualHost *:80>\n  ServerName ignored.example.com\n</VirtualHost>\n");
        EnabledLink("loaded.conf", "../sites-available/loaded.conf");
        EnabledLink("010-renamed.conf", "/etc/apache2/sites-available/linked.conf");

        var sites = ApacheDisabledSites.Read(_available, _enabled, ReadStandInLink);

        Assert.Equal(
            [("aetheus-api.example.com", 443, "/var/www/api"), ("alt.example.com", 8443, ""), ("aetheus-api.example.com", 80, "")],
            sites.Select(site => (site.ServerName, site.Port, site.DocumentRoot)).ToArray());
        Assert.All(sites, site => Assert.False(site.IsEnabled));
        Assert.Equal(Path.Combine(_available, "aetheus-api.example.com.conf"), sites[2].ConfigFile);
    }

    [Fact]
    public void AnUnsafeName_ALink_AndAnOversizedFile_AreNotRead()
    {
        Available("bad name.conf", "<VirtualHost *:80>\n  ServerName bad.example.com\n</VirtualHost>\n");
        Available("huge.conf", "<VirtualHost *:80>\n  ServerName huge.example.com\n</VirtualHost>\n"
            + new string('#', (int)ApacheDisabledSites.MaxSiteFileBytes));
        Available("ok.conf", "<VirtualHost _default_>\n  ServerName ok.example.com\n</VirtualHost>\n");

        var sites = ApacheDisabledSites.Read(_available, _enabled, ReadStandInLink);

        var site = Assert.Single(sites);
        Assert.Equal(("ok.example.com", 80), (site.ServerName, site.Port));
    }

    [Fact]
    public async Task TheCollector_AddsTheDisabledSites_AfterTheLoadedOnes()
    {
        Available("live.conf", "<VirtualHost *:80>\n  ServerName live.example.com\n</VirtualHost>\n");
        Available("old.conf", "<VirtualHost *:80>\n  ServerName old.example.com\n</VirtualHost>\n");
        File.WriteAllText(Path.Combine(_enabled, "live.conf"), "<VirtualHost *:80>\n  ServerName live.example.com\n</VirtualHost>\n");
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, string.Empty, string.Empty));
        shell.RunExecAsync("which", Arg.Is<IReadOnlyList<string>>(a => a.Contains("apache2")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "/usr/sbin/apache2", string.Empty));
        shell.RunExecAsync("which", Arg.Is<IReadOnlyList<string>>(a => a.Contains("apache2ctl")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "/usr/sbin/apache2ctl", string.Empty));
        shell.RunExecAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.Contains("-S")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "port 80 namevhost live.example.com (/etc/apache2/sites-enabled/live.conf:1)", string.Empty));
        var collector = new ApacheCollector(NullLogger<ApacheCollector>.Instance, shell)
        {
            IsWindows = false,
            DirectoryExists = Directory.Exists,
            SitesAvailableDirectory = _available,
            SitesEnabledDirectory = _enabled,
            ReadLinkTarget = ReadStandInLink
        };

        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [("live.example.com", true), ("old.example.com", false)],
            result.VirtualHosts.Select(vhost => (vhost.ServerName, vhost.IsEnabled)).ToArray());
    }
}
