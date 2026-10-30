// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Users;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Users;

/// <summary>R-7: Users, Organizations and Roles are three OE tabs, each tab a route; the search field of
/// the old bar is gone (each grid filters itself).</summary>
public class AdminIdentityNavTests : BunitContext
{
    public AdminIdentityNavTests()
    {
        BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    [Fact]
    public void ThreeTabs_AreShown_TheActiveOneSelected_WithNoSearchField()
    {
        var nav = Render<AdminIdentityNav>(parameters => parameters.Add(component => component.ActiveSection, "organizations"));

        var tabs = nav.FindAll("[role=tab]");
        Assert.Equal(["Users", "Organizations", "Roles"], tabs.Select(tab => tab.TextContent.Trim()));
        Assert.Equal("Organizations", tabs.Single(tab => tab.GetAttribute("aria-selected") == "true").TextContent.Trim());
        Assert.Empty(nav.FindAll("input"));
    }

    [Theory]
    [InlineData("users", "Roles", "/admin/roles")]
    [InlineData("roles", "Users", "/admin/users")]
    [InlineData("users", "Organizations", "/admin/organizations")]
    public void SelectingATab_NavigatesToItsRoute(string active, string clicked, string route)
    {
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var nav = Render<AdminIdentityNav>(parameters => parameters.Add(component => component.ActiveSection, active));

        nav.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == clicked).Click();

        Assert.EndsWith(route, navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectingTheActiveTab_StaysOnThePage()
    {
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var before = navigation.Uri;
        var nav = Render<AdminIdentityNav>(parameters => parameters.Add(component => component.ActiveSection, "users"));

        nav.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == "Users").Click();

        Assert.Equal(before, navigation.Uri);
    }
}
