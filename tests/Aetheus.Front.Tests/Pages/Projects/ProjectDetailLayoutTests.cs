// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Projects;

public sealed class ProjectDetailLayoutTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectDetailLayoutTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Fact]
    public async Task Loaded_Project_Name_Replaces_The_Route_Skeleton()
    {
        _handler.SetJsonResponse("api/projects/42", new ProjectDetailDto
        {
            Id = 42,
            Name = "Atlas",
            Status = ProjectStatus.Active
        });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        var loader = Services.GetRequiredService<ProjectDetailLoader>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/projects/42/overview");

        var cut = Render<ProjectDetailLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<div/>")));
        await cut.InvokeAsync(() => loader.EnsureLoadedAsync(42));

        cut.WaitForAssertion(() =>
        {
            var owner = Services.GetRequiredService<BreadcrumbService>().Items[1];
            Assert.Equal("Atlas", owner.Text);
            Assert.False(owner.IsLoading);
        });
    }

    /// <summary>From a project page outside this layout (the standalone settings page) to a section, the router mounts
    /// the layout inside the navigation event, before OE's breadcrumb service handles the same event and puts the route
    /// fallback back. The loader already holds the project, so no load event follows: the fallback itself must carry
    /// the project's name, else the crumb stays a skeleton for good (PageRules, /projects/{Id}/environments).</summary>
    [Fact]
    public async Task Project_Crumb_Keeps_The_Name_When_The_Layout_Mounts_Before_The_Trail_Resets()
    {
        _handler.SetJsonResponse("api/projects/42", new ProjectDetailDto { Id = 42, Name = "Atlas", Status = ProjectStatus.Active });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/projects/42/edit");
        // The router subscribes to the navigation before any page resolves the breadcrumb service.
        IRenderedComponent<ProjectDetailLayout>? mounted = null;
        nav.LocationChanged += (_, _) => mounted ??= Render<ProjectDetailLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<div/>")));
        var loader = Services.GetRequiredService<ProjectDetailLoader>();
        await loader.EnsureLoadedAsync(42, Xunit.TestContext.Current.CancellationToken);
        var breadcrumb = Services.GetRequiredService<BreadcrumbService>();

        await Renderer.Dispatcher.InvokeAsync(() => nav.NavigateTo("/projects/42/environments"));

        Assert.NotNull(mounted);
        var owner = breadcrumb.Items[1];
        Assert.Equal("Atlas", owner.Text);
        Assert.False(owner.IsLoading);
    }

    /// <summary>Load follow state once with the project so Follow and Edit are both present when
    /// the menu opens. Reopening the menu sends no new request.</summary>
    [Fact]
    public async Task HeaderMenu_ShowsFollowBeforeSettings_AtOnce_WithOneReadOfTheState()
    {
        _handler.SetJsonResponse("api/projects/42", new ProjectDetailDto
        {
            Id = 42,
            Name = "Atlas",
            Status = ProjectStatus.Active
        });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        var loader = Services.GetRequiredService<ProjectDetailLoader>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/projects/42/overview");

        var cut = Render<ProjectDetailLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<div/>")));
        await cut.InvokeAsync(() => loader.EnsureLoadedAsync(42));
        cut.WaitForAssertion(() => Assert.Single(_handler.Requests, request => request.Url.Contains("api/notifications/me/subscriptions")));

        cut.WaitForElement(".omni-overflow-menu__trigger").Click();
        var labels = cut.FindAll(".omni-menu__item .omni-menu__label").Select(label => label.TextContent.Trim()).ToList();
        // Recette R3-70: the project's settings entry reads Settings, not Edit.
        Assert.Equal(["Subscribe", "Settings"], labels);

        cut.Find(".omni-overflow-menu__trigger").Click();
        cut.Find(".omni-overflow-menu__trigger").Click();
        Assert.Equal(2, cut.FindAll(".omni-menu__item").Count);
        Assert.Single(_handler.Requests, request => request.Url.Contains("api/notifications/me/subscriptions"));
    }

    /// <summary>The project's settings are a menu entry, not a second header button (the button shows only when
    /// Settings is the section's main action, R-32).</summary>
    [Fact]
    public async Task R68_SettingsIsAMenuEntryOnly_WhenTheSectionHasItsOwnAction()
    {
        _handler.SetJsonResponse("api/projects/42", new ProjectDetailDto
        {
            Id = 42,
            Name = "Atlas",
            Status = ProjectStatus.Active
        });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        var loader = Services.GetRequiredService<ProjectDetailLoader>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/projects/42/overview");

        var cut = Render<ProjectDetailLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<div/>")));
        await cut.InvokeAsync(() => loader.EnsureLoadedAsync(42));

        // No header button (the header never holds a row of buttons): Settings lives in the menu only.
        cut.WaitForElement(".omni-overflow-menu__trigger").Click();
        cut.WaitForAssertion(() => Assert.Contains("Settings", cut.FindAll(".omni-menu__item .omni-menu__label").Select(label => label.TextContent.Trim())));
        Assert.Empty(cut.FindAll("[data-testid='project-settings-button']"));

        cut.FindAll(".omni-menu__item").First(item => item.TextContent.Trim() == "Settings").Click();
        Assert.EndsWith("/projects/42/edit", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    /// <summary>Recette R-32: on the monitoring page, Settings is the blue main action of the header and Add sits in
    /// the menu; the other project pages keep their Secondary Settings button.</summary>
    [Fact]
    public async Task R32_MonitoringPage_SettingsIsThePrimaryButton_AndAddIsInTheMenu()
    {
        _handler.SetJsonResponse("api/projects/42", new ProjectDetailDto { Id = 42, Name = "Atlas", Status = ProjectStatus.Active });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        _handler.SetJsonResponse("api/appmonitoring/projects/42/apps", new List<MonitoredAppDto>());
        var loader = Services.GetRequiredService<ProjectDetailLoader>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/projects/42/monitoring");

        var cut = Render<ProjectDetailLayout>(parameters => parameters
            .Add(layout => layout.Body, builder =>
            {
                builder.OpenComponent<MonitoredAppsList>(0);
                builder.AddAttribute(1, nameof(MonitoredAppsList.ProjectId), 42);
                builder.CloseComponent();
            }));
        await cut.InvokeAsync(() => loader.EnsureLoadedAsync(42));

        cut.WaitForAssertion(() =>
        {
            var button = cut.Find("[data-testid='project-settings-button']");
            Assert.Contains("primary", button.ClassName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secondary", button.ClassName, StringComparison.OrdinalIgnoreCase);
        });
        // Settings is the only visible action of the header; Add is in the menu, before Settings.
        Assert.Empty(cut.FindAll("[data-testid='project-settings-button'] ~ button"));
        cut.Find(".omni-overflow-menu__trigger").Click();
        var entries = cut.WaitForElements(".omni-menu__item .omni-menu__label").Select(label => label.TextContent.Trim()).ToList();
        Assert.Contains("AddMonitoredApp", entries);
        Assert.True(entries.IndexOf("AddMonitoredApp") < entries.IndexOf("Settings"));
    }

    /// <summary>Recette R-68: a project waiting for its cleanup has no settings (like its menu).</summary>
    [Fact]
    public async Task R68_NoSettingsButton_WhileTheProjectWaitsForItsCleanup()
    {
        _handler.SetJsonResponse("api/projects/42", new ProjectDetailDto
        {
            Id = 42,
            Name = "Atlas",
            Status = ProjectStatus.Active,
            DeletionRequestedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc)
        });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>());
        var loader = Services.GetRequiredService<ProjectDetailLoader>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/projects/42/overview");

        var cut = Render<ProjectDetailLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<div/>")));
        await cut.InvokeAsync(() => loader.EnsureLoadedAsync(42));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='project-deletion-pending']")));
        Assert.Empty(cut.FindAll("[data-testid='project-settings-button']"));
    }
}
