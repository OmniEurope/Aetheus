// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Layout;

/// <summary>Recette R3-29: the start-up offers the browser the logical parent of the page the document
/// was opened on, through the shell as its splash goes, and only for a signed-in session.</summary>
public sealed class DeepLinkParentEntryTests : BunitContext
{
    private const string Seed = "Aetheus.seedParentHistoryEntry";

    [Fact]
    public async Task SeedAsync_OnASectionPage_OffersItsAbsoluteParent()
    {
        BunitTestHelper.RegisterServices(this);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/servers/5/mail");
        JSInterop.Setup<bool>(Seed, _ => true).SetResult(true);

        var seeded = await DeepLinkParentEntry.SeedAsync(nav, JSInterop.JSRuntime);

        Assert.True(seeded);
        var invocation = Assert.Single(JSInterop.Invocations, call => call.Identifier == Seed);
        Assert.Equal("http://localhost/servers/5/overview", invocation.Arguments[0]);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login?returnUrl=%2Fservers%2F5%2Fmail")]
    public async Task SeedAsync_OnAPageWithoutParent_LeavesTheHistoryAlone(string path)
    {
        BunitTestHelper.RegisterServices(this);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo(path);

        var seeded = await DeepLinkParentEntry.SeedAsync(nav, JSInterop.JSRuntime);

        Assert.False(seeded);
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == Seed);
    }

    [Fact]
    public async Task MainLayout_SignedIn_SeedsTheParentOnceBeforeTheSplashGoes()
    {
        BunitTestHelper.RegisterServices(this);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/servers/5/services");
        var cut = Render<MainLayout>();

        await cut.InvokeAsync(() => cut.Instance.OnViewportChanged(false));
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, call => call.Identifier == "Aetheus.hideSplash"));
        await cut.InvokeAsync(() => cut.Instance.OnViewportChanged(true));
        cut.Render();

        var identifiers = JSInterop.Invocations.Select(call => call.Identifier).ToList();
        var seed = Assert.Single(JSInterop.Invocations, call => call.Identifier == Seed);
        Assert.Equal("http://localhost/servers/5/overview", seed.Arguments[0]);
        Assert.True(identifiers.IndexOf(Seed) < identifiers.IndexOf("Aetheus.hideSplash"),
            "The parent entry must be in the history before the user can press Back.");
    }

    [Fact]
    public async Task MainLayout_SignedOut_DoesNotSeed()
    {
        BunitTestHelper.RegisterServices(this, authenticated: false);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/servers/5/services");
        var cut = Render<MainLayout>();

        await cut.InvokeAsync(() => cut.Instance.OnViewportChanged(false));
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, call => call.Identifier == "Aetheus.hideSplash"));

        Assert.Contains("login", nav.Uri, StringComparison.Ordinal);
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == Seed);
    }
}
