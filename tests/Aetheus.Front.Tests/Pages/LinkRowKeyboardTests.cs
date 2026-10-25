// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;
using HelpCenterPage = Aetheus.Front.Components.Help.HelpCenter;
using ProjectsPage = Aetheus.Front.Components.Projects.Projects;

namespace Aetheus.Front.Tests.Pages;

/// <summary>A tile, card or table row that behaves as a link opens its target on Enter or Space from
/// the keyboard, as a click does; any other key leaves the page where it is.</summary>
public sealed class LinkRowKeyboardTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public LinkRowKeyboardTests() => _handler = BunitTestHelper.RegisterServices(this);

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    [Fact]
    public void HelpCard_IsARealLinkToTheArticle_SoTheBrowserOpensItOnEnter()
    {
        // The card is itself the link (OmniCard.Href): a real, focusable anchor the browser opens on Enter,
        // not a div acting as a button with a key handler of its own.
        _handler.SetJsonResponse("help/help-", new List<HelpArticle> { new("servers", "Servers", "Manage servers", "rzi-server", []) });
        var cut = Render<HelpCenterPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("a.help-card")));

        var card = cut.Find("a.help-card");
        Assert.Equal("/help/servers", card.GetAttribute("href"));
        Assert.Empty(cut.FindAll("div[role='button']"));
        Assert.Null(card.GetAttribute("role"));
        Assert.Contains("Servers", card.TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Enter", true)]
    [InlineData(" ", true)]
    [InlineData("Escape", false)]
    public async Task ProjectTile_OpensTheProjectOnEnterOrSpaceOnly(string key, bool navigates)
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 7, Name = "Alpha", Status = ProjectStatus.Active, Tags = [] }],
            TotalCount = 1
        });
        var cut = Render<ProjectsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='project-item']")));
        var before = Nav.Uri;

        await cut.Find("[data-testid='project-item']").KeyDownAsync(new KeyboardEventArgs { Key = key });

        if (navigates) Assert.EndsWith("/projects/7", Nav.Uri, StringComparison.Ordinal);
        else Assert.Equal(before, Nav.Uri);
    }

    [Theory]
    [InlineData("Enter", true)]
    [InlineData(" ", true)]
    [InlineData("x", false)]
    public async Task OnboardingRow_OpensItsLinkOnEnterOrSpaceOnly(string key, bool navigates)
    {
        var steps = new List<OnboardingStep>
        {
            new() { Title = "Repo", Description = "Connect", Icon = "folder", State = OnboardingStepState.Todo, Href = "/projects/9/repo", ActionLabel = "Go" },
        };
        var cut = Render<OnboardingStepList>(p => p
            .Add(c => c.Title, "Setup")
            .Add(c => c.Description, "Steps")
            .Add(c => c.Steps, steps));
        var before = Nav.Uri;

        await cut.Find("[data-testid='onboarding-step']").KeyDownAsync(new KeyboardEventArgs { Key = key });

        if (navigates) Assert.EndsWith("/projects/9/repo", Nav.Uri, StringComparison.Ordinal);
        else Assert.Equal(before, Nav.Uri);
    }

    [Fact]
    public async Task OnboardingRow_ABlockedStepHasNoLink_SoEnterGoesNowhere()
    {
        var steps = new List<OnboardingStep>
        {
            new() { Title = "Repo", Description = "Connect", Icon = "folder", State = OnboardingStepState.Blocked, Href = "/projects/9/repo", ActionLabel = "Go" },
        };
        var cut = Render<OnboardingStepList>(p => p
            .Add(c => c.Title, "Setup")
            .Add(c => c.Description, "Steps")
            .Add(c => c.Steps, steps));
        var before = Nav.Uri;

        await cut.Find("[data-testid='onboarding-step']").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal(before, Nav.Uri);
    }
}
