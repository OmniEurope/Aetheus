// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// Direct unit coverage for the <see cref="UrlSyncedTabs"/> slug→index mapping (the URL-sync contract).
/// Exercises the private <c>IndexForSlug</c> via reflection so the assertions are behavioural and not
/// tied to a tabs render (bUnit plus a tab strip makes a rendered assertion fragile). Guards the
/// silent-fallback edge the challenge flagged: an unknown / typo'd / empty slug must resolve to tab 0,
/// never throw nor land on the wrong tab.
/// </summary>
public class UrlSyncedTabsTests : BunitContext
{
    public UrlSyncedTabsTests() => BunitTestHelper.RegisterServices(this);
    private static readonly string[] Slugs = ["overview", "history", "settings"];

    private static int IndexForSlug(string? slug)
    {
        var component = new UrlSyncedTabs();
        // Set via reflection (not an object initializer) to avoid BL0005 - the component-parameter
        // analyzer rejects assigning a [Parameter] from outside the component in source.
        typeof(UrlSyncedTabs).GetProperty(nameof(UrlSyncedTabs.Slugs))!.SetValue(component, Slugs);
        var method = typeof(UrlSyncedTabs).GetMethod("IndexForSlug", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (int)method.Invoke(component, [slug])!;
    }

    [Theory]
    [InlineData("overview", 0)]
    [InlineData("history", 1)]
    [InlineData("settings", 2)]
    [InlineData("HISTORY", 1)] // case-insensitive match
    public void IndexForSlug_ResolvesKnownSlug(string slug, int expected)
        => Assert.Equal(expected, IndexForSlug(slug));

    [Theory]
    [InlineData("does-not-exist")]
    [InlineData("")]
    [InlineData(null)]
    public void IndexForSlug_UnknownOrEmpty_FallsBackToZero(string? slug)
        => Assert.Equal(0, IndexForSlug(slug));

    // --- OnParametersSet state-machine regression coverage ---------------------------------------
    // Drives the private OnParametersSet directly (no tabs render needed - it reads only Tab,
    // SelectedIndex and the private index/last fields). Locks the "Generate Token" regression: a bare
    // parent re-render on a non-default tab must NOT snap an unbound page back to tab 0.

    private static UrlSyncedTabs NewComponent()
    {
        var c = new UrlSyncedTabs();
        typeof(UrlSyncedTabs).GetProperty(nameof(UrlSyncedTabs.Slugs))!.SetValue(c, Slugs);
        // The injected services the component reads outside a render (SITE-FUNC-NAVIGATION-001 logging).
        typeof(UrlSyncedTabs).GetProperty("Nav", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(c, new RecordingNavigationManager("http://localhost/", "http://localhost/pipelines/runs/8"));
        typeof(UrlSyncedTabs).GetProperty("Logger", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(c, Microsoft.Extensions.Logging.Abstractions.NullLogger<UrlSyncedTabs>.Instance);
        return c;
    }

    private static void SetParam(UrlSyncedTabs c, string name, object? value)
        => typeof(UrlSyncedTabs).GetProperty(name)!.SetValue(c, value);

    private static void OnParametersSet(UrlSyncedTabs c)
        => typeof(UrlSyncedTabs).GetMethod("OnParametersSet", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(c, null);

    private static int Index(UrlSyncedTabs c)
        => (int)typeof(UrlSyncedTabs).GetField("_index", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(c)!;

    [Fact]
    public void ReRender_OnNonDefaultTab_KeepsTab()
    {
        var c = NewComponent();
        // Initial navigation to ?tab=settings (index 2).
        SetParam(c, nameof(UrlSyncedTabs.Tab), "settings");
        OnParametersSet(c);
        Assert.Equal(2, Index(c));

        // Bare parent re-render: URL unchanged, SelectedIndex still at its unbound 0 default.
        // Must stay on the settings tab, NOT snap back to General (index 0).
        OnParametersSet(c);
        Assert.Equal(2, Index(c));
    }

    [Fact]
    public void ProgrammaticSelectedIndexChange_IsHonoured()
    {
        var c = NewComponent();
        SetParam(c, nameof(UrlSyncedTabs.Tab), "settings");
        OnParametersSet(c);
        Assert.Equal(2, Index(c));

        // A page that binds SelectedIndex drives the tab programmatically (URL unchanged).
        SetParam(c, nameof(UrlSyncedTabs.SelectedIndex), 1);
        OnParametersSet(c);
        Assert.Equal(1, Index(c));
    }

    [Fact]
    public void InitialProgrammaticSelectedIndex_IsHonoured_WhenUrlHasNoExplicitTab()
    {
        var c = NewComponent();
        SetParam(c, nameof(UrlSyncedTabs.SelectedIndex), 1);

        OnParametersSet(c);

        Assert.Equal(1, Index(c));
    }

    /// <summary>Nested inside the pipelines hub, the URL carries the hub's slug (?tab=used). It
    /// names no tab of the inner set, so the inner set's remembered tab still applies.</summary>
    [Fact]
    public void InitialProgrammaticSelectedIndex_IsHonoured_WhenUrlTabBelongsToAnotherTabSet()
    {
        var c = NewComponent();
        SetParam(c, nameof(UrlSyncedTabs.Tab), "used");
        SetParam(c, nameof(UrlSyncedTabs.SelectedIndex), 1);

        OnParametersSet(c);

        Assert.Equal(1, Index(c));
    }

    [Fact]
    public void ExplicitUrlTab_WinsOverInitialProgrammaticSelectedIndex()
    {
        var c = NewComponent();
        SetParam(c, nameof(UrlSyncedTabs.Tab), "overview");
        SetParam(c, nameof(UrlSyncedTabs.SelectedIndex), 1);

        OnParametersSet(c);

        Assert.Equal(0, Index(c));
    }

    [Fact]
    public void UrlTabChange_WinsOverStaleIndex()
    {
        var c = NewComponent();
        SetParam(c, nameof(UrlSyncedTabs.Tab), "settings");
        OnParametersSet(c);
        Assert.Equal(2, Index(c));

        // Back/forward drops the query param -> default tab (index 0) wins.
        SetParam(c, nameof(UrlSyncedTabs.Tab), null);
        OnParametersSet(c);
        Assert.Equal(0, Index(c));
    }

    [Fact]
    public async Task UserSelection_PushesShareableUrl_AndBackRestoresPreviousTab()
    {
        var c = NewComponent();
        var nav = new RecordingNavigationManager("http://localhost/", "http://localhost/pipelines/runs/8");
        typeof(UrlSyncedTabs).GetProperty("Nav", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(c, nav);

        OnParametersSet(c);
        var change = typeof(UrlSyncedTabs).GetMethod("OnChangeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)change.Invoke(c, [2])!;

        Assert.Single(nav.History);
        Assert.Equal("http://localhost/pipelines/runs/8?tab=settings", nav.History[0]);

        // Blazor supplies the pushed query value after NavigateTo.
        SetParam(c, nameof(UrlSyncedTabs.Tab), "settings");
        OnParametersSet(c);
        Assert.Equal(2, Index(c));

        // Browser back supplies the previous query value (none) to the component.
        SetParam(c, nameof(UrlSyncedTabs.Tab), null);
        OnParametersSet(c);
        Assert.Equal(0, Index(c));
    }

    [Fact]
    public async Task DirectUrlAndBackNavigation_InvokeChangeCallback()
    {
        var changes = new List<int>();
        var component = NewComponent();
        SetParam(component, nameof(UrlSyncedTabs.Change),
            Microsoft.AspNetCore.Components.EventCallback.Factory.Create<int>(changes, changes.Add));
        SetParam(component, nameof(UrlSyncedTabs.Tab), "settings");
        OnParametersSet(component);
        await InvokeParametersSetAsync(component);

        Assert.Equal([2], changes);

        SetParam(component, nameof(UrlSyncedTabs.Tab), null);
        OnParametersSet(component);
        await InvokeParametersSetAsync(component);

        Assert.Equal([2, 0], changes);
    }

    private static Task InvokeParametersSetAsync(UrlSyncedTabs component) =>
        (Task)typeof(UrlSyncedTabs)
            .GetMethod("OnParametersSetAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(component, null)!;

    private sealed class RecordingNavigationManager : Microsoft.AspNetCore.Components.NavigationManager
    {
        public RecordingNavigationManager(string baseUri, string uri) => Initialize(baseUri, uri);

        public List<string> History { get; } = [];

        protected override void NavigateToCore(string uri, Microsoft.AspNetCore.Components.NavigationOptions options)
        {
            var absolute = ToAbsoluteUri(uri).ToString();
            History.Add(absolute);
            Uri = absolute;
        }
    }
}
