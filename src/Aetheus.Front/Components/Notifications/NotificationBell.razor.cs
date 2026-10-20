// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Notifications;

public partial class NotificationBell : IDisposable
{
    [Inject] private UserNotificationsFeed Feed { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;

    private bool _popoverOpen;
    private bool _busy;
    private bool _followsNothing;
    private bool _followingAll;

    protected override async Task OnInitializedAsync()
    {
        Feed.OnChanged += OnFeedChanged;
        await Feed.EnsureStartedAsync();
    }

    private void OnFeedChanged() => _ = InvokeAsync(StateHasChanged);


    /// <summary> Each opening reads whether the user follows any project, because a user who
    /// follows none receives nothing and the panel must say why. Read at opening only: following a
    /// project happens elsewhere (its page, the preferences), and the next opening sees it.
    /// </summary>
    private async Task OnOpenChangedAsync(bool open)
    {
        _popoverOpen = open;
        if (!open) return;
        try
        {
            _followsNothing = (await Api.Notifications.GetSubscriptionsAsync()).Count == 0;
        }
        catch (HttpRequestException)
        {
            // Unknown: no hint rather than a wrong one.
            _followsNothing = false;
        }
    }

    private async Task FollowAllAsync()
    {
        if (_followingAll) return;
        _followingAll = true;
        try
        {
            var subscriptions = await Api.Notifications.FollowAllProjectsAsync();
            if (subscriptions is null)
            {
                Toast.Error("Error", "OperationFailed");
                return;
            }
            _followsNothing = subscriptions.Count == 0;
            Toast.Success("Saved", "NotificationFollowedAllProjects", subscriptions.Count);
        }
        finally
        {
            _followingAll = false;
        }
    }

    // The popover's links navigate by themselves; choosing one only closes the popover.
    private void ClosePopover() => _popoverOpen = false;

    private async Task MarkAllReadAsync()
    {
        _busy = true;
        try
        {
            // Success is visible in the count and row state without a toast covering the panel;
            // failures still display feedback.
            if (!await Feed.MarkAllReadAsync())
                Toast.Error("Error", "OperationFailed");
        }
        finally
        {
            _busy = false;
        }
    }

    public void Dispose() => Feed.OnChanged -= OnFeedChanged;
}
