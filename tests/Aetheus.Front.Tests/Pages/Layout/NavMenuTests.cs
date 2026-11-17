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

    /// <summary>R-36: the planner is a global entry, after Projects and the mailboxes and before Notifications and
    /// Settings; it is no longer inside the Projects submenu.</summary>
    [Fact]
    public void ThePlanner_IsAGlobalEntry_BetweenTheMailboxesAndTheNotifications()
    {
        var cut = Render<NavMenu>();

        var hrefs = cut.FindAll("a[href]").Select(link => link.GetAttribute("href")).ToList();
        var projects = hrefs.IndexOf("projects");
        var mail = hrefs.IndexOf("mail");
        var planner = hrefs.IndexOf("pipelines/schedule");
        var settings = hrefs.IndexOf("settings");
        Assert.True(projects >= 0 && mail > projects, "the mailboxes must follow Projects");
        Assert.True(planner > mail, "the planner must follow the mailboxes");
        Assert.True(settings > planner, "Settings must come after the planner (and the notifications between them)");
        Assert.Contains("PipelineSchedule", cut.Find("a[href='pipelines/schedule']").TextContent, StringComparison.Ordinal);
        Assert.True(planner > hrefs.IndexOf("servers"), "the planner is listed after the Servers and Projects sections");
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

    private static AnalysisPortfolioProjectDto Project(int id, AnalysisGrade? grade, int critical = 0, int high = 0) => new()
    {
        ProjectId = id,
        ProjectName = $"P{id}",
        CriticalCount = critical,
        HighCount = high,
        Grade = grade is null ? null : new AnalysisGradeSummaryDto { OverallGrade = grade }
    };

    /// <summary>R-6: the project menu Quality entry shows its grade letter, never a red badge in its place, even with an
    /// open critical finding (it is counted in the grade itself, R-7).</summary>
    [Theory]
    [InlineData(AnalysisGrade.B, 0, 0)]
    [InlineData(AnalysisGrade.B, 0, 2)]
    [InlineData(AnalysisGrade.A, 1, 0)]
    [InlineData(AnalysisGrade.F, 4, 0)]
    public void ProjectQualityEntry_ShowsTheGradeLetter_NeverARedBadge(AnalysisGrade grade, int critical, int high)
    {
        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto> { Project(7, grade, critical, high) });
        Services.GetRequiredService<NavigationManager>().NavigateTo("projects/7/overview");

        var cut = Render<NavMenu>();

        cut.WaitForAssertion(() =>
        {
            var entry = cut.Find("a[href=\"projects/7/quality\"]");
            Assert.Equal(grade.ToString(), entry.QuerySelector(".grade-badge")!.TextContent.Trim());
        });
    }

    /// <summary>R-6: the main menu Quality entry shows the lowest grade of the projects (here B beside an A), whatever
    /// the critical counts, and no badge when no project has a grade.</summary>
    [Theory]
    [InlineData(AnalysisGrade.B, 0, "B")]
    [InlineData(AnalysisGrade.B, 3, "B")]
    [InlineData(AnalysisGrade.F, 0, "F")]
    public void MainQualityEntry_ShowsTheLowestGradeOfTheProjects(AnalysisGrade grade, int critical, string expected)
    {
        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto> { Project(1, AnalysisGrade.A), Project(2, grade, critical) });
        Services.GetRequiredService<NavigationManager>().NavigateTo("projects");

        var cut = Render<NavMenu>();

        cut.WaitForAssertion(() =>
        {
            var entry = cut.Find("a[href=\"analysis\"]");
            Assert.Equal(expected, entry.QuerySelector(".grade-badge")!.TextContent.Trim());
        });
    }

    [Fact]
    public void MainQualityEntry_HasNoBadge_WhenNoProjectHasAGrade()
    {
        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto> { Project(1, null, critical: 2) });
        Services.GetRequiredService<NavigationManager>().NavigateTo("projects");

        var cut = Render<NavMenu>();

        cut.WaitForAssertion(() => Assert.Null(cut.Find("a[href=\"analysis\"]").QuerySelector(".grade-badge")));
    }

    /// <summary>User decision 2026-11-10: the menu keeps the current grade. A finding decided or reopened (the notifier
    /// fires) makes it re-read the summary at once, instead of keeping the letter read when the page was entered.</summary>
    [Fact]
    public void ProjectQualityEntry_ReadsTheGradeAgain_WhenAFindingDecisionMovesIt()
    {
        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto> { Project(7, AnalysisGrade.B) });
        Services.GetRequiredService<NavigationManager>().NavigateTo("projects/7/overview");
        var cut = Render<NavMenu>();
        cut.WaitForAssertion(() =>
            Assert.Equal("B", cut.Find("a[href=\"projects/7/quality\"]").QuerySelector(".grade-badge")!.TextContent.Trim()));

        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto> { Project(7, AnalysisGrade.A) });
        cut.InvokeAsync(() => Services.GetRequiredService<Aetheus.Front.Components.Shared.QualityChangeNotifier>().Notify());

        cut.WaitForAssertion(() =>
            Assert.Equal("A", cut.Find("a[href=\"projects/7/quality\"]").QuerySelector(".grade-badge")!.TextContent.Trim()),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ProjectQualityEntry_ReadsTheGradeAgain_WhenTheUserOpensTheProjectOverview()
    {
        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto> { Project(7, AnalysisGrade.B) });
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("projects/7/pipelines");
        var cut = Render<NavMenu>();
        cut.WaitForAssertion(() =>
            Assert.Equal("B", cut.Find("a[href=\"projects/7/quality\"]").QuerySelector(".grade-badge")!.TextContent.Trim()));

        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto> { Project(7, AnalysisGrade.A) });
        navigation.NavigateTo("projects/7/overview");

        cut.WaitForAssertion(() =>
            Assert.Equal("A", cut.Find("a[href=\"projects/7/quality\"]").QuerySelector(".grade-badge")!.TextContent.Trim()));
    }

    [Fact]
    public void Dispose_StopsListeningToTheQualityNotifier()
    {
        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto> { Project(7, AnalysisGrade.B) });
        Services.GetRequiredService<NavigationManager>().NavigateTo("projects/7/overview");
        var cut = Render<NavMenu>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("a[href=\"projects/7/quality\"]")));

        cut.Instance.Dispose();

        // A leftover subscription would read the summary again and re-render a disposed component.
        var requestsBefore = _handler.RequestsSnapshot().Count(request => request.Url.Contains("api/analysis/portfolio/projects", StringComparison.Ordinal));
        var rendersBefore = cut.RenderCount;
        Services.GetRequiredService<Aetheus.Front.Components.Shared.QualityChangeNotifier>().Notify();
        Assert.Equal(requestsBefore, _handler.RequestsSnapshot().Count(request => request.Url.Contains("api/analysis/portfolio/projects", StringComparison.Ordinal)));
        Assert.Equal(rendersBefore, cut.RenderCount);
    }

    private static ServerQualityDto ServerQuality(int id, AnalysisGrade? grade) => new() { ServerId = id, ServerName = $"srv-{id}", Grade = grade };

    /// <summary>R-7: the Servers menu and its Quality entry show the lowest grade of the servers, and the Quality entry of a
    /// server its own grade; the Projects menu keeps only the projects' grades, whatever the servers' grades.</summary>
    [Fact]
    public void ServersMenu_ReflectsTheServersGrade_AndTheProjectsMenuOnlyTheProjects()
    {
        _handler.SetJsonResponse("api/servers/host-quality", new List<ServerQualityDto> { ServerQuality(1, AnalysisGrade.A), ServerQuality(2, AnalysisGrade.F) });
        _handler.SetJsonResponse("api/analysis/portfolio/projects", new List<AnalysisPortfolioProjectDto> { Project(1, AnalysisGrade.B) });
        Services.GetRequiredService<NavigationManager>().NavigateTo("servers");

        var cut = Render<NavMenu>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("F", cut.Find("a[href=\"servers\"]").QuerySelector(".grade-badge")!.TextContent.Trim());
            Assert.Equal("F", cut.Find("a[href=\"servers/quality\"]").QuerySelector(".grade-badge")!.TextContent.Trim());
            // The projects' Quality entry is the lowest grade of the projects (B), not the F of a server.
            Assert.Equal("B", cut.Find("a[href=\"analysis\"]").QuerySelector(".grade-badge")!.TextContent.Trim());
        });
    }

    [Fact]
    public void ServerQualityEntry_ShowsThatServersGrade()
    {
        _handler.SetJsonResponse("api/servers/host-quality", new List<ServerQualityDto> { ServerQuality(1, AnalysisGrade.A), ServerQuality(2, AnalysisGrade.C) });
        Services.GetRequiredService<NavigationManager>().NavigateTo("servers/2/overview");

        var cut = Render<NavMenu>();

        cut.WaitForAssertion(() => Assert.Equal("C", cut.Find("a[href=\"servers/2/quality\"]").QuerySelector(".grade-badge")!.TextContent.Trim()));
    }

    [Fact]
    public void ServersMenu_HasNoBadge_WhenNoServerHasAGrade()
    {
        _handler.SetJsonResponse("api/servers/host-quality", new List<ServerQualityDto> { ServerQuality(1, null) });
        Services.GetRequiredService<NavigationManager>().NavigateTo("servers");

        var cut = Render<NavMenu>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("a[href=\"servers/quality\"]")));
        Assert.Null(cut.Find("a[href=\"servers/quality\"]").QuerySelector(".grade-badge"));
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
