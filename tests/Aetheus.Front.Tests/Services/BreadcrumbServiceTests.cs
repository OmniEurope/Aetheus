// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Aetheus.Front.Resources;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;

namespace Aetheus.Front.Tests;

public class BreadcrumbServiceTests : BunitContext
{
    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    /// <summary>The Aetheus facade over OE's trail, the one OmniPageHeader reads.</summary>
    private BreadcrumbService Create(IOmniBreadcrumbResolver? resolver = null) =>
        new(new OmniBreadcrumbService(Nav, resolver), Nav, Substitute.For<IStringLocalizer<AppStrings>>());

    private sealed class Fallback(Func<string, IReadOnlyList<OmniBreadcrumbEntry>> resolve) : IOmniBreadcrumbResolver
    {
        public IReadOnlyList<OmniBreadcrumbEntry> Resolve(string relativePath) => resolve(relativePath);
    }

    [Fact]
    public void Items_InitiallyEmpty()
    {
        var sut = Create();
        Assert.Empty(sut.Items);
    }

    [Fact]
    public void Set_AddsItemsAndFiresOnChanged()
    {
        var sut = Create();
        var changed = false;
        sut.OnChanged += () => changed = true;

        sut.Set(new BreadcrumbItem("Home", "/"), new BreadcrumbItem("Servers", "/servers"));

        Assert.Equal(2, sut.Items.Count);
        Assert.Equal("Home", sut.Items[0].Text);
        Assert.Equal("/servers", sut.Items[1].Href);
        Assert.True(changed);
    }

    [Fact]
    public void Clear_RemovesItemsAndFiresOnChanged()
    {
        var sut = Create();
        sut.Set(new BreadcrumbItem("Home", "/"));

        var changed = false;
        sut.OnChanged += () => changed = true;

        sut.Clear();

        Assert.Empty(sut.Items);
        Assert.True(changed);
    }

    [Fact]
    public void Set_ReplacesExistingItems()
    {
        var sut = Create();
        sut.Set(new BreadcrumbItem("Old"));

        sut.Set(new BreadcrumbItem("New"));

        Assert.Single(sut.Items);
        Assert.Equal("New", sut.Items[0].Text);
    }

    [Fact]
    public void ParentHref_ReturnsNearestClickableAncestor()
    {
        var sut = Create();
        sut.Set(
            new BreadcrumbItem("Projects", "/projects"),
            new BreadcrumbItem("Project without a link"),
            new BreadcrumbItem("Pipelines", "/pipelines?projectId=42"),
            new BreadcrumbItem("Current pipeline"));

        Assert.Equal("/pipelines?projectId=42", sut.ParentHref);
    }

    [Fact]
    public void ParentHref_DoesNotUseCurrentItemHref()
    {
        var sut = Create();
        sut.Set(new BreadcrumbItem("Current page", "/current-page"));

        Assert.Null(sut.ParentHref);
    }

    [Fact]
    public void LocationChanged_ClearsItemsAndFiresOnChanged()
    {
        var sut = Create();
        sut.Set(new BreadcrumbItem("Home", "/"));

        var changed = false;
        sut.OnChanged += () => changed = true;

        Nav.NavigateTo("/other-page");

        Assert.Empty(sut.Items);
        Assert.True(changed);
    }

    [Fact]
    public void LocationChanged_WithFallback_ReplacesItemsWithoutAnEmptyState()
    {
        var sut = Create(new Fallback(path => [new OmniBreadcrumbEntry($"fallback:{path}")]));
        sut.Set(new BreadcrumbItem("Loaded page"));

        Nav.NavigateTo("/other-page");

        var item = Assert.Single(sut.Items);
        Assert.Contains("other-page", item.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void LocationChanged_QueryOnly_KeepsItems()
    {
        // A tab switch via UrlSyncedTabs changes only the ?tab= query on the SAME path. The breadcrumb,
        // set once by the owning page behind an early-return guard, must survive that navigation - the
        // regression the user reported ("le fil d'Ariane est perdu dès qu'on clique sur un onglet").
        var sut = Create();
        Nav.NavigateTo("/users/5");
        sut.Set(new BreadcrumbItem("Users", "/users"), new BreadcrumbItem("alice"));

        var changed = false;
        sut.OnChanged += () => changed = true;

        Nav.NavigateTo("/users/5?tab=roles");

        Assert.Equal(2, sut.Items.Count);
        Assert.False(changed);
    }

    [Fact]
    public void LocationChanged_TrailingSlashOnly_KeepsItems()
    {
        // A stray trailing slash (from a link/redirect) is the same logical page - it must not read as a
        // page change and wipe the breadcrumb.
        var sut = Create();
        Nav.NavigateTo("/settings");
        sut.Set(new BreadcrumbItem("Settings"));

        Nav.NavigateTo("/settings/");

        Assert.Single(sut.Items);
    }

    [Fact]
    public void LocationChanged_PathChangeFromTab_ClearsItems()
    {
        // Leaving the page entirely (path changes, not just the query) must still reset so the next
        // page starts clean.
        var sut = Create();
        Nav.NavigateTo("/users/5?tab=roles");
        sut.Set(new BreadcrumbItem("Users", "/users"), new BreadcrumbItem("alice"));

        Nav.NavigateTo("/servers");

        Assert.Empty(sut.Items);
    }

    [Fact]
    public void DisposingTheTrail_UnsubscribesFromLocationChanged()
    {
        var trail = new OmniBreadcrumbService(Nav);
        var sut = new BreadcrumbService(trail, Nav, Substitute.For<IStringLocalizer<AppStrings>>());
        sut.Set(new BreadcrumbItem("Home", "/"));

        trail.Dispose();

        var changed = false;
        sut.OnChanged += () => changed = true;
        Nav.NavigateTo("/after-dispose");

        Assert.Single(sut.Items);
        Assert.False(changed);
    }

    [Fact]
    public void BreadcrumbItem_RecordEquality()
    {
        var a = new BreadcrumbItem("Home", "/");
        var b = new BreadcrumbItem("Home", "/");
        Assert.Equal(a, b);
    }
}
