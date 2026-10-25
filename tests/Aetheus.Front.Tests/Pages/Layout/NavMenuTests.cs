// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NSubstitute;

namespace Aetheus.Front.Tests.Pages;

public class NavMenuTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public NavMenuTests()
    {
        // Register AlertNotificationService first - it's needed by NavMenu
        // We must create it before RegisterServices freezes the container
        var authProvider = new AuthStateProvider(Substitute.For<IJSRuntime>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthStateProvider>.Instance);
        typeof(AuthStateProvider).GetProperty("Token")!.SetValue(authProvider, "test-token");
        typeof(AuthStateProvider).GetProperty("Roles")!.SetValue(authProvider, new List<string> { "Admin" });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = "http://test:5301" })
            .Build();

        var hubFactory = new HubConnectionFactory(
            config,
            authProvider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthDelegatingHandler>.Instance);
        var alertService = new AlertNotificationService(authProvider, hubFactory);
        Services.AddSingleton(alertService);

        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
    }

    [Fact]
    public void Renders_MenuItems()
    {
        var cut = Render<NavMenu>();
        Assert.Contains("Dashboard", cut.Markup);
        Assert.Contains("Servers", cut.Markup);
        Assert.Contains("Projects", cut.Markup);
        Assert.Contains("Settings", cut.Markup);
        Assert.Contains("HelpCenter", cut.Markup);
    }

    [Fact]
    public void AlertsLink_NavigatesWithoutAnUnreadBadge()
    {
        var alertService = Services.GetRequiredService<AlertNotificationService>();
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("servers");
        var cut = Render<NavMenu>();

        var alerts = cut.Find("a[href='alerts']");
        Assert.Null(alerts.GetAttribute("aria-describedby"));
        Assert.Empty(cut.FindAll("#nav-alert-count"));

        // The entry is a plain link the browser follows (bUnit does not), so follow it as the router would.
        cut.InvokeAsync(() => nav.NavigateTo(alerts.GetAttribute("href")!));

        Assert.Equal("alerts", nav.ToBaseRelativePath(nav.Uri));
        Assert.Equal(0, alertService.UnreadCount);
        Assert.Empty(cut.FindAll("#nav-alert-count"));
    }

    [Fact]
    public void AdminUser_RendersAdminSection()
    {
        var cut = Render<NavMenu>();
        Assert.Contains("Administration", cut.Markup);
    }

    [Fact]
    public void Renders_HelpCenter()
    {
        var cut = Render<NavMenu>();
        Assert.Contains("HelpCenter", cut.Markup);
    }

    [Theory]
    [InlineData("projects")]
    [InlineData("analysis")]
    [InlineData("backups")]
    public void ProjectGlobalMenu_HidesDeferredEntries(string route)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(route);

        var cut = Render<NavMenu>();

        Assert.Contains("href=\"analysis\"", cut.Markup);
        Assert.DoesNotContain("href=\"backups\"", cut.Markup);
        Assert.DoesNotContain("href=\"service-connections\"", cut.Markup);
        Assert.DoesNotContain("href=\"ai-tasks\"", cut.Markup);
    }

    [Fact]
    public void ProjectDetailMenu_HidesDeferredEntriesAndKeepsAnalysis()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("projects/7/overview");

        var cut = Render<NavMenu>();

        Assert.Contains("projects/7/quality", cut.Markup);
        Assert.DoesNotContain("projects/7/board", cut.Markup);
        Assert.DoesNotContain("projects/7/backups", cut.Markup);
        Assert.DoesNotContain("projects/7/ai-tasks", cut.Markup);
        Assert.DoesNotContain("href=\"analysis\"", cut.Markup);
        Assert.DoesNotContain("href=\"backups\"", cut.Markup);
    }

    [Fact]
    public void Dispose_UnsubscribesEvents()
    {
        var cut = Render<NavMenu>();
        var projectNav = Services.GetRequiredService<ProjectNavContextService>();

        var instance = (IDisposable)cut.Instance;
        instance.Dispose();

        // After Dispose, NavMenu is unsubscribed from ProjectNav.OnChanged: firing it must NOT drive
        // another render (a leftover subscription would re-render a disposed component).
        var afterDispose = cut.RenderCount;
        projectNav.Set(99);
        Assert.Equal(afterDispose, cut.RenderCount);
    }

    // ──── Server sub-menu capabilities (fake server scenario) ────

    [Fact]
    public void ServerSubMenu_ShowsAllSectionLinks()
    {
        // All server section links remain visible regardless of reported capabilities.
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("servers/7/overview");

        var cut = Render<NavMenu>();

        Assert.Contains("servers/7/overview", cut.Markup);
        Assert.Contains("servers/7/services", cut.Markup);
        Assert.Contains("servers/7/projects", cut.Markup);
        Assert.Contains("servers/7/pipelines", cut.Markup);
        Assert.Contains("servers/7/libraries", cut.Markup);
        Assert.Contains("servers/7/vaults", cut.Markup);
        Assert.Contains("servers/7/releases", cut.Markup);
        Assert.Contains("servers/7/tasks", cut.Markup);
        Assert.Contains("servers/7/logs", cut.Markup);
    }

    [Fact]
    public void ServerSubMenu_NoModuleSectionsInNav()
    {
        // Module sections (docker, apache, certbot…) are consolidated under
        // the Services tab - individual routes don't appear in the nav.
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("servers/3/overview");

        var cut = Render<NavMenu>();

        Assert.Contains("servers/3/services", cut.Markup);
        Assert.DoesNotContain("servers/3/apache", cut.Markup);
        Assert.DoesNotContain("servers/3/certbot", cut.Markup);
        Assert.DoesNotContain("servers/3/mail", cut.Markup);
        Assert.DoesNotContain("servers/3/docker", cut.Markup);
    }

    // R1-28: the server's own mail entry, under Services, only once its mail stack is installed.
    private async Task SeedOpenServerAsync(int id, bool mailInstalled)
    {
        var server = Servers.ServerTestData.MakeMailServer(installed: mailInstalled) with { Id = id };
        _handler.SetJsonResponse($"api/servers/{id}", server);
        _handler.SetJsonResponse($"api/monitoring/servers/{id}/metrics", new List<Aetheus.Shared.Components.Monitoring.ServerMetricDto>());
        var loader = Services.GetRequiredService<Aetheus.Front.Components.Shared.ServerDetailLoader>();
        await loader.EnsureLoadedAsync(id);
    }

    [Fact]
    public async Task R1_28_ServerSubMenu_ListsTheMailEntryRightAfterServices_WhenTheMailStackIsInstalled()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("servers/7/overview");
        await SeedOpenServerAsync(7, mailInstalled: true);

        var cut = Render<NavMenu>();

        var hrefs = cut.FindAll("a[href^='servers/7/']").Select(link => link.GetAttribute("href")).ToList();
        var services = hrefs.IndexOf("servers/7/services");
        Assert.True(services >= 0);
        Assert.Equal("servers/7/mail", hrefs[services + 1]);
        Assert.Contains("Mail", cut.Find("a[href='servers/7/mail']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R1_28_ServerSubMenu_HidesTheMailEntry_WhenNoMailStackIsInstalled()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("servers/7/overview");
        await SeedOpenServerAsync(7, mailInstalled: false);

        var cut = Render<NavMenu>();

        Assert.Empty(cut.FindAll("a[href='servers/7/mail']"));
    }

    [Fact]
    public async Task R1_28_ServerSubMenu_HidesTheMailEntry_WhenTheLoadedServerIsAnotherOne()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("servers/7/overview");
        await SeedOpenServerAsync(3, mailInstalled: true);

        var cut = Render<NavMenu>();

        Assert.Empty(cut.FindAll("a[href='servers/7/mail']"));
    }
}
