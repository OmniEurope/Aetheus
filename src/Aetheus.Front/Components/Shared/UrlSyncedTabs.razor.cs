// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Tabs wrapper that keeps the active tab in the URL query (<c>?tab=slug</c>) so the browser
/// back/forward buttons navigate between tabs and a tab is shareable/bookmarkable. The URL is the
/// source of truth: a user tab click pushes a history entry, and back/forward re-applies the tab via
/// <see cref="SupplyParameterFromQueryAttribute"/> → <see cref="OnParametersSet"/>.
/// </summary>
public partial class UrlSyncedTabs : ComponentBase, IDisposable
{
    [Inject] private NavigationManager Nav { get; set; } = default!;

    // SITE-FUNC-NAVIGATION-001 (instrumented, not fixed): quick back/forward navigations on mobile threw
    // InvalidOperation_EnumFailedVersion from SupplyParameterFromQueryValueProvider.OnLocationChanged, the
    // framework enumerating its ?query subscribers while one of them (this component is one) was mounted
    // or disposed. A bUnit run does not reproduce it (its renderer defers the re-render past the
    // enumeration), so each URL-driven tab change and each disposal is logged with the URL, giving the
    // order of events around the next occurrence.
    [Inject] private ILogger<UrlSyncedTabs> Logger { get; set; } = default!;

    /// <summary>Ordered slugs of the tabs actually rendered, matching the <c>&lt;Tabs&gt;</c> order.
    /// The first slug is the default tab and uses a clean URL (no query parameter).</summary>
    [Parameter] public IReadOnlyList<string> Slugs { get; set; } = [];

    /// <summary>The tab item list rendered in the <c>&lt;Tabs&gt;</c> slot. OE selects a tab by its key, so
    /// each <c>OmniTabsItem</c> carries <c>Key</c> equal to its slug; a tab without it could never be
    /// shown selected.</summary>
    [Parameter] public RenderFragment? Tabs { get; set; }

    /// <summary>Forwarded to the inner tabs component.</summary>
    [Parameter] public string? Class { get; set; }

    [Parameter] public bool RenderAllPanels { get; set; }

    private string CssClass => string.IsNullOrWhiteSpace(Class)
        ? "url-synced-tabs"
        : $"url-synced-tabs {Class}";

    /// <summary>Optional two-way bound index for pages that read the active tab elsewhere.</summary>
    [Parameter] public int SelectedIndex { get; set; }
    [Parameter] public EventCallback<int> SelectedIndexChanged { get; set; }

    /// <summary>Raised when the selected tab changes, for example to lazy-load its data.</summary>
    [Parameter] public EventCallback<int> Change { get; set; }

    [Parameter, SupplyParameterFromQuery(Name = "tab")] public string? Tab { get; set; }

    private int _index;
    private string? _lastTab;
    private bool _tabInitialized;
    private int _lastSelectedIndex;
    private bool _notifyUrlChange;

    protected override void OnParametersSet()
    {
        // The URL is the source of truth: whenever the ?tab value actually changes (first load,
        // back/forward, or the param being dropped) it wins - and an empty param maps to the default
        // tab (index 0), so navigating from ?tab=coverage back to the clean URL resets to the default
        // instead of stranding the page on the stale tab. When the URL did NOT change, honour a
        // programmatic SelectedIndex ONLY if it actually changed (a page that binds it drove the tab).
        // Otherwise KEEP the current tab: a bare parent re-render (async data reload, SignalR push, a
        // toast) must not snap an unbound page back to its default tab - SelectedIndex sits at its 0
        // default on those pages, and blindly copying it here reset the active tab to 0 on every
        // re-render (the aetheus-qa Settings "Generate Token" regression: the tokens grid vanished
        // because the tab jumped back to General mid-interaction).
        int target;
        var urlChanged = !_tabInitialized || !string.Equals(Tab, _lastTab, StringComparison.Ordinal);
        if (urlChanged)
        {
            // Honor the parent's initial default when no URL tab belongs to this set, including a
            // slug owned by an enclosing set; subsequent navigation to a clean URL resets to the
            // first tab.
            target = !_tabInitialized && !IsKnownSlug(Tab) && SelectedIndex != 0
                ? SelectedIndex
                : IndexForSlug(Tab);
            Logger.LogInformation(
                "UrlSyncedTabs [{Slugs}] URL {Uri}: tab '{PreviousTab}' (index {PreviousIndex}) -> '{Tab}' (index {Index}), first pass {FirstPass}",
                string.Join(",", Slugs), Nav.Uri, _lastTab, _index, Tab, target, !_tabInitialized);
            _lastTab = Tab;
            _tabInitialized = true;
        }
        else if (SelectedIndex != _lastSelectedIndex)
        {
            target = SelectedIndex;
        }
        else
        {
            target = _index;
        }
        _lastSelectedIndex = SelectedIndex;
        _index = target;
        _notifyUrlChange = urlChanged;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_notifyUrlChange) return;
        _notifyUrlChange = false;
        if (_index != SelectedIndex && SelectedIndexChanged.HasDelegate)
            await SelectedIndexChanged.InvokeAsync(_index);
        if (Change.HasDelegate) await Change.InvokeAsync(_index);
    }

    /// <summary>The key of the selected tab, which is its slug; null before any slug is known.</summary>
    private string? SelectedKey => _index >= 0 && _index < Slugs.Count ? Slugs[_index] : null;

    private Task OnValueChangedAsync(string? key) => OnChangeAsync(IndexForSlug(key));

    private async Task OnChangeAsync(int index)
    {
        if (index == _index) return;
        _index = index;
        _lastSelectedIndex = index;
        // First tab → clean URL (drop the param); others → ?tab=slug. NavigateTo pushes a history entry.
        var slug = index > 0 && index < Slugs.Count ? Slugs[index] : null;
        // Do not pre-acknowledge the slug here. SupplyParameterFromQuery must observe it as a real URL
        // change on the next parameter pass; otherwise an unbound SelectedIndex (default 0) wins and
        // snaps the visible tab back while the address bar keeps the newly selected slug.
        Nav.NavigateTo(Nav.GetUriWithQueryParameter("tab", slug));
        if (SelectedIndexChanged.HasDelegate) await SelectedIndexChanged.InvokeAsync(index);
        if (Change.HasDelegate) await Change.InvokeAsync(index);
    }

    public void Dispose()
    {
        Logger.LogInformation(
            "UrlSyncedTabs [{Slugs}] disposed at URL {Uri} on tab '{Tab}' (index {Index})",
            string.Join(",", Slugs), Nav.Uri, _lastTab, _index);
        GC.SuppressFinalize(this);
    }

    private bool IsKnownSlug(string? slug) =>
        !string.IsNullOrEmpty(slug) && Slugs.Any(known => string.Equals(known, slug, StringComparison.OrdinalIgnoreCase));

    private int IndexForSlug(string? slug)
    {
        if (string.IsNullOrEmpty(slug)) return 0;
        for (var i = 0; i < Slugs.Count; i++)
            if (string.Equals(Slugs[i], slug, StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
    }
}
