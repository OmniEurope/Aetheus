// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Dashboards;

/// <summary>R-43: what the dashboard shows a user who can read no server, project nor pipeline.</summary>
public partial class MinimalDashboard : IDisposable
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ActiveOrganizationService Organizations { get; set; } = default!;
    [Inject] private UserNotificationsFeed Feed { get; set; } = default!;

    /// <summary>No organization and no notification: the page says there is nothing to show.</summary>
    private bool HasNothing => Organizations.Active is null && Feed.Latest.Count == 0;

    protected override async Task OnInitializedAsync()
    {
        Organizations.Changed += OnChanged;
        Feed.OnChanged += OnChanged;
        await Feed.EnsureStartedAsync();
    }

    private void OnChanged() => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Organizations.Changed -= OnChanged;
        Feed.OnChanged -= OnChanged;
    }
}
