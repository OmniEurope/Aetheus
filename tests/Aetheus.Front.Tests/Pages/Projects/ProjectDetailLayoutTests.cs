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
}
