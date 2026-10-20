// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

/// <summary>One header model everywhere, OE's <see cref="OmniPageHeader"/> (title, then the trail
/// under it), and the outlet through which the pipelines list puts its New button into the host's
/// header actions.</summary>
public sealed class PageHeaderActionsSectionTests : BunitContext
{
    public PageHeaderActionsSectionTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void Header_KeepsTheTrailUnderTheTitle()
    {
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("/projects/2/overview");

        var cut = Render<OmniPageHeader>(parameters => parameters.Add(header => header.Subtitle, "Description"));

        Assert.Null(cut.Find(".omni-page-header__row").QuerySelector("nav"));
        Assert.NotNull(cut.Find(".omni-page-header__trail nav"));
        Assert.Single(cut.FindAll(".omni-page-header__subtitle"));
    }

    [Fact]
    public void ActionsSection_ShowsTheActionsTheBodyContributes()
    {
        RenderFragment body = builder => builder.AddMarkupContent(0, "<button id=\"body-new\">New</button>");
        RenderFragment actions = builder =>
        {
            builder.OpenComponent<SectionOutlet>(0);
            builder.AddAttribute(1, nameof(SectionOutlet.SectionName), "test-actions");
            builder.CloseComponent();
        };

        var cut = Render(builder =>
        {
            builder.OpenComponent<OmniPageHeader>(0);
            builder.AddAttribute(1, nameof(OmniPageHeader.Actions), actions);
            builder.CloseComponent();
            builder.OpenComponent<SectionContent>(2);
            builder.AddAttribute(3, nameof(SectionContent.SectionName), "test-actions");
            builder.AddAttribute(4, nameof(SectionContent.ChildContent), body);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".omni-page-header__actions #body-new")));
    }
}
