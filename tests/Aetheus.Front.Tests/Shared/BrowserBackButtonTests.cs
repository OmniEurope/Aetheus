// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

public class BrowserBackButtonTests : BunitContext
{
    public BrowserBackButtonTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void Click_WithBreadcrumbParent_NavigatesToParentInsteadOfBrowserHistory()
    {
        var breadcrumb = Services.GetRequiredService<BreadcrumbService>();
        breadcrumb.Set(
            new BreadcrumbItem("Projects", "/projects"),
            new BreadcrumbItem("Toto", "/projects/42/overview"),
            new BreadcrumbItem("Pipelines", "/pipelines?projectId=42"),
            new BreadcrumbItem("Deploy"));

        var cut = Render<BrowserBackButton>();

        cut.Find("button").Click();

        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("/pipelines?projectId=42", nav.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "Aetheus.goBack");
    }

    [Fact]
    public void Click_WithoutBreadcrumbParent_UsesBrowserHistoryBack()
    {
        Services.GetRequiredService<BreadcrumbService>()
            .Set(new BreadcrumbItem("Current page"));
        JSInterop.SetupVoid("Aetheus.goBack");
        var cut = Render<BrowserBackButton>(parameters => parameters
            .Add(component => component.Text, "Back")
            .Add(component => component.Title, "Go back"));

        cut.Find("button").Click();

        JSInterop.VerifyInvoke("Aetheus.goBack");
    }

    [Fact]
    public void Click_OnAPageOpenedFromAPastedAddress_NavigatesToTheRouteParentWithoutTheHistory()
    {
        // R3-29: the trail is the route-derived one OE installs before the page names its ancestors.
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/servers/5/mail");
        var cut = Render<BrowserBackButton>();

        cut.Find("button").Click();

        Assert.Equal("http://localhost/servers/5/overview", nav.Uri);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "Aetheus.goBack");
    }

    [Fact]
    public void Click_OnTheServerOverview_SkipsTheServerCrumbThatLinksToTheOverviewItself()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/servers/5/overview");
        Services.GetRequiredService<BreadcrumbService>().Set(
            new BreadcrumbItem("Servers", "/servers"),
            new BreadcrumbItem("web-01", "/servers/5/overview"),
            new BreadcrumbItem("Overview"));
        var cut = Render<BrowserBackButton>();

        cut.Find("button").Click();

        Assert.Equal("http://localhost/servers", nav.Uri);
    }

    [Fact]
    public void Click_OnATopLevelPageWithoutAncestorLink_NavigatesToTheDashboard()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/servers");
        var cut = Render<BrowserBackButton>();

        cut.Find("button").Click();

        Assert.Equal("http://localhost/", nav.Uri);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "Aetheus.goBack");
    }
}
