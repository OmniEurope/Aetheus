// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R-15, R-14: the files behind a domain are named right under the domain in the lists of sites, without hovering
/// anything (production grouped <c>aetheus-api.sonytumen.com.conf</c> under <c>api.aetheus.sonytumen.com</c>), and each
/// list carries the help that says how to retire an old file and what Aetheus cannot touch.
/// </summary>
public sealed class ApacheSiteFileLinesTests : BunitContext
{
    public ApacheSiteFileLinesTests() => BunitTestHelper.RegisterServices(this);

    private static ApacheVirtualHostDto Block(string file, int port, bool enabled, string owner = "") => new()
    {
        ServerName = "api.example.test",
        Port = port,
        IsEnabled = enabled,
        ConfigFile = $"/etc/apache2/sites-available/{file}",
        ConfigFileForeignOwner = owner
    };

    private IRenderedComponent<ServerApacheSection> RenderSection(params ApacheVirtualHostDto[] hosts) =>
        Render<ServerApacheSection>(parameters => parameters
            .Add(section => section.ServerId, 41)
            .Add(section => section.Server, new ServerDetailDto
            {
                Id = 41,
                Name = "web-srv",
                Hostname = "10.0.0.5",
                Type = ServerType.Normal,
                Status = ServerStatus.Online,
                Tags = [],
                Services = [],
                Docker = new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] },
                Apache = new ApacheDataDto { IsInstalled = true, IsRunning = true, VirtualHosts = [.. hosts] }
            }));

    [Fact]
    public void R15_EachFileOfADomain_IsNamedUnderTheDomain_WithItsPorts()
    {
        var cut = RenderSection(
            Block("old-api.example.test.conf", 80, enabled: true),
            Block("api.example.test.conf", 80, enabled: true),
            Block("api.example.test-ssl.conf", 443, enabled: true));

        cut.WaitForAssertion(() =>
        {
            var lines = cut.FindAll("[data-testid='apache-site-file']").Select(line => line.TextContent.Trim()).ToList();
            Assert.Equal(3, lines.Count);
            Assert.Contains("old-api.example.test.conf (80)", lines);
            Assert.Contains("api.example.test.conf (80)", lines);
            Assert.Contains("api.example.test-ssl.conf (443)", lines);
        });
    }

    [Fact]
    public void R15_AFileServingTwoPorts_IsOneLineWithBothPorts()
    {
        var cut = RenderSection(
            Block("api.example.test.conf", 80, enabled: true),
            Block("api.example.test.conf", 443, enabled: true));

        cut.WaitForAssertion(() =>
            Assert.Equal("api.example.test.conf (80, 443)", Assert.Single(cut.FindAll("[data-testid='apache-site-file']")).TextContent.Trim()));
    }

    [Fact]
    public void R15_ADisabledSite_ShowsItsFilesToo()
    {
        var cut = RenderSection(Block("old-api.example.test.conf", 80, enabled: false));

        cut.WaitForAssertion(() =>
            Assert.Equal("old-api.example.test.conf (80)", Assert.Single(cut.FindAll("[data-testid='apache-site-file']")).TextContent.Trim()));
    }

    [Fact]
    public void R14_AFileAetheusCannotDelete_CarriesItsOwnerInABadge_OnTheLineOfThatFileOnly()
    {
        var cut = RenderSection(
            Block("old-api.example.test.conf", 80, enabled: false, owner: "root"),
            Block("api.example.test.conf", 80, enabled: true),
            Block("api.example.test.conf", 443, enabled: true));

        cut.WaitForAssertion(() =>
        {
            var badge = Assert.Single(cut.FindAll("[data-testid='apache-site-file-owner']"));
            Assert.Equal("root", badge.TextContent.Trim());
            Assert.Contains("ApacheFileOwnerTooltip", badge.GetAttribute("title"), StringComparison.Ordinal);
            // The badge sits beside the line of the file it describes, which keeps its own text.
            Assert.Equal("old-api.example.test.conf (80)",
                badge.ParentElement!.QuerySelector("[data-testid='apache-site-file']")!.TextContent.Trim());
        });
    }

    [Fact]
    public void R14_WhenNoOwnerIsKnown_NoBadgeIsShown_ForEnabledAndDisabledSites()
    {
        // An agent that does not report the owner (older) or that owns the file sends no owner: nothing is guessed.
        var cut = RenderSection(
            Block("old-api.example.test.conf", 80, enabled: false),
            Block("api.example.test.conf", 80, enabled: true, owner: "  "));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='apache-site-file']").Count));
        Assert.Empty(cut.FindAll("[data-testid='apache-site-file-owner']"));
    }

    [Fact]
    public void R14_OnAnEnabledSite_TheOwnerBadgeShowsToo()
    {
        var cut = RenderSection(Block("api.example.test.conf", 80, enabled: true, owner: "certbot"));

        cut.WaitForAssertion(() => Assert.Equal("certbot", Assert.Single(cut.FindAll("[data-testid='apache-site-file-owner']")).TextContent.Trim()));
    }

    [Fact]
    public void R14_BothListsCarryTheHelpOnRetiringAFileAndOnFilesAetheusDoesNotOwn()
    {
        var cut = RenderSection(Block("api.example.test.conf", 80, enabled: true));

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='apache-site-files-help']").Count));
        Assert.Contains("ApacheSiteFilesHelp", cut.Find("[data-testid='apache-site-files-help']").GetAttribute("aria-label"), StringComparison.Ordinal);
    }
}
