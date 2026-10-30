// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Recette R-103: one reader is named and linked; two or more are a clickable number badge
/// carrying the total, whose dialog lists every reader as a link.</summary>
public sealed class ResourceUsageSummaryTests : BunitContext
{
    private readonly IRenderedComponent<OmniComponentsHost> _host;

    public ResourceUsageSummaryTests()
    {
        BunitTestHelper.RegisterServices(this);
        _host = Render<OmniComponentsHost>(p => p.Add(h => h.OverlayService, Services.GetRequiredService<OmniOverlayService>()));
    }

    private static ResourceUsageUserDto Pipeline(int id, string name) =>
        new() { Kind = ResourceUsageKind.Pipeline, Id = id, Name = name, ProjectId = 1 };

    private IRenderedComponent<ResourceUsageSummary> RenderSummary(params ResourceUsageUserDto[] users) =>
        Render<ResourceUsageSummary>(p => p.Add(x => x.Users, users).Add(x => x.Loaded, true));

    [Fact]
    public void OneReader_ShowsItsNameAsALink_AndNoBadge()
    {
        var cut = RenderSummary(Pipeline(10, "deploy-prod"));

        Assert.Equal("/pipelines/10", cut.Find("a").GetAttribute("href"));
        Assert.Contains("deploy-prod", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void TwoReaders_ShowOnlyTheTotalInAButton_NoNames()
    {
        var cut = RenderSummary(Pipeline(10, "deploy-prod"), Pipeline(11, "release-fast"));

        Assert.Equal("2", cut.Find("button.omni-badge").TextContent.Trim());
        Assert.False(string.IsNullOrWhiteSpace(cut.Find("button").GetAttribute("aria-label")));
        Assert.DoesNotContain("deploy-prod", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("release-fast", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("a"));
    }

    [Fact]
    public void ClickingTheBadge_OpensADialogListingEveryReaderAsALink()
    {
        var cut = RenderSummary(Pipeline(10, "deploy-prod"), Pipeline(11, "release-fast"), Pipeline(12, "nightly"));

        cut.Find("button").Click();

        _host.WaitForAssertion(() => Assert.NotEmpty(_host.FindAll(".omni-dialog")));
        Assert.Equal("ResourceUsageDialogTitle", _host.Find(".omni-dialog__title").TextContent);
        var links = _host.FindAll(".omni-dialog a");
        Assert.Equal(["/pipelines/10", "/pipelines/11", "/pipelines/12"], links.Select(l => l.GetAttribute("href")));
        Assert.Equal(["deploy-prod", "release-fast", "nightly"], links.Select(l => l.TextContent.Trim()));
        Assert.NotEmpty(_host.FindAll(".omni-dialog__close"));
    }

    [Fact]
    public void FollowingALinkOfTheDialog_ClosesIt()
    {
        var cut = RenderSummary(Pipeline(10, "deploy-prod"), Pipeline(11, "release-fast"));
        cut.Find("button").Click();
        _host.WaitForAssertion(() => Assert.NotEmpty(_host.FindAll(".omni-dialog a")));

        _host.FindAll(".omni-dialog a")[1].Click();

        _host.WaitForAssertion(() => Assert.Empty(_host.FindAll(".omni-dialog")));
    }

    [Fact]
    public void NoReader_KeepsTheUnusedBadge_AndNotLoadedDrawsNothing()
    {
        var unused = RenderSummary();
        Assert.Contains("ResourceUnused", unused.Markup, StringComparison.Ordinal);
        Assert.Empty(unused.FindAll("button"));

        var notLoaded = Render<ResourceUsageSummary>(p => p.Add(x => x.Loaded, false));
        Assert.Equal(string.Empty, notLoaded.Markup.Trim());
    }
}
