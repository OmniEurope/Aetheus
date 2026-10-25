// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Recette R-12: a disabled site can be deleted from the Apache page, file by file, after a destructive
/// confirmation; an enabled site offers no deletion.</summary>
public sealed class ServerApacheSiteDeleteTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerApacheSiteDeleteTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/servers/1/apache/action", true);
    }

    private ImmediateDialogService Confirmation =>
        (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    [Fact]
    public void ADisabledSite_OffersToDeleteEachOfItsFiles_AndAnEnabledSiteOffersNone()
    {
        var cut = Render(
            Vhost("old.example.com", 80, "/etc/apache2/sites-available/old.conf", enabled: false),
            Vhost("old.example.com", 443, "/etc/apache2/sites-available/old-ssl.conf", enabled: false),
            Vhost("live.example.com", 443, "/etc/apache2/sites-available/live.conf", enabled: true));

        foreach (var trigger in cut.FindAll("tbody .omni-overflow-menu__trigger").ToList())
            trigger.Click();

        var items = cut.FindAll(".omni-menu__item").Where(item => item.TextContent.Trim() == "ApacheDeleteSiteItem").ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Contains("omni-menu__item--danger", item.ClassName, StringComparison.Ordinal));
        Assert.Equal(["old.conf", "old-ssl.conf"],
            ServerApacheSection.SiteFiles(new ServerApacheSection.ApacheVirtualHostGroup(
                "old.example.com",
                [
                    Vhost("old.example.com", 80, "/etc/apache2/sites-available/old.conf", enabled: false),
                    Vhost("old.example.com", 443, "/etc/apache2/sites-available/old-ssl.conf", enabled: false),
                    Vhost("old.example.com", 8080, "/etc/apache2/sites-available/old.conf", enabled: false)
                ])));
    }

    [Fact]
    public void DeletingASite_AsksWithTheRedVerb_ThenPostsDeleteSiteForThatFile()
    {
        var cut = Render(
            Vhost("old.example.com", 80, "/etc/apache2/sites-available/old.conf", enabled: false),
            Vhost("old.example.com", 443, "/etc/apache2/sites-available/old-ssl.conf", enabled: false));

        DeleteItem(cut, 1).Click();
        // Declined: nothing is sent; the question is destructive and names its verb.
        Assert.DoesNotContain(_handler.RequestDetails, request => request.Url.Contains("apache/action"));
        Assert.Equal("ApacheDeleteSiteConfirm", Confirmation.LastConfirmMessage);
        Assert.Equal("Delete", Confirmation.LastConfirmOptions!.OkButtonText);
        Assert.True(Confirmation.LastConfirmOptions.Destructive);

        Confirmation.ConfirmResult = true;
        DeleteItem(cut, 1).Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request =>
            request.Method == "POST" && request.Url.Contains("api/servers/1/apache/action")
            && request.Body!.Contains($"\"action\":{(int)ApacheAction.DeleteSite}", StringComparison.Ordinal)
            && request.Body.Contains("\"targetName\":\"old-ssl.conf\"", StringComparison.Ordinal)));
    }

    private static AngleSharp.Dom.IElement DeleteItem(IRenderedComponent<ServerApacheSection> cut, int index)
    {
        if (cut.FindAll(".omni-menu__item").Count == 0)
            cut.Find("tbody .omni-overflow-menu__trigger").Click();
        return cut.FindAll(".omni-menu__item").Where(item => item.TextContent.Trim() == "ApacheDeleteSiteItem").ElementAt(index);
    }

    private static ApacheVirtualHostDto Vhost(string name, int port, string file, bool enabled) => new()
    {
        ServerName = name,
        Port = port,
        DocumentRoot = "/var/www/" + name,
        ConfigFile = file,
        IsEnabled = enabled
    };

    private IRenderedComponent<ServerApacheSection> Render(params ApacheVirtualHostDto[] vhosts) =>
        Render<ServerApacheSection>(parameters => parameters
            .Add(section => section.Server, new ServerDetailDto
            {
                Id = 1,
                Name = "apache-server",
                Hostname = "apache.example.com",
                Type = ServerType.Normal,
                Status = ServerStatus.Online,
                Tags = [],
                Services = [],
                Apache = new ApacheDataDto { IsInstalled = true, IsRunning = true, VirtualHosts = [.. vhosts] }
            })
            .Add(section => section.ServerId, 1));
}
