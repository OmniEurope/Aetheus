// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Notifications;

public partial class NotificationPreferencesPanel
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private List<NotificationPreferenceDto> _preferences = [];
    private List<ProjectSubscriptionDto> _subscriptions = [];
    private bool _loading = true;
    private bool _loadFailed;
    private bool _followingAll;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var preferencesTask = Api.Notifications.GetPreferencesAsync();
            var subscriptionsTask = Api.Notifications.GetSubscriptionsAsync();
            await Task.WhenAll(preferencesTask, subscriptionsTask);
            _preferences = await preferencesTask;
            _subscriptions = await subscriptionsTask;
        }
        catch (HttpRequestException)
        {
            _loadFailed = true;
        }
        finally
        {
            _loading = false;
        }
    }

    private sealed record PreferenceGroup(string TitleKey, List<NotificationPreferenceDto> Items);

    /// <summary>Pipelines, quality, supervision, then everything else, in that order.</summary>
    private IEnumerable<PreferenceGroup> Groups =>
        _preferences
            .GroupBy(preference => GroupKey(preference.EventType))
            .OrderBy(group => Array.IndexOf(GroupOrder, group.Key))
            .Select(group => new PreferenceGroup(group.Key, group.ToList()));

    private static readonly string[] GroupOrder = ["Pipelines", "Releases", "Quality", "Monitoring", "Servers", "OtherEvents"];

    internal static string GroupKey(string eventType) => eventType switch
    {
        _ when eventType.StartsWith("pipeline.", StringComparison.Ordinal) => "Pipelines",
        _ when eventType.StartsWith("release.", StringComparison.Ordinal) => "Releases",
        _ when eventType.StartsWith("agent-update.", StringComparison.Ordinal) => "Servers",
        _ when eventType.StartsWith("analysis.", StringComparison.Ordinal) => "Quality",
        _ when eventType.StartsWith("app.", StringComparison.Ordinal)
               || eventType.StartsWith("alert.", StringComparison.Ordinal) => "Monitoring",
        _ => "OtherEvents"
    };

    /// <summary>An event whose payload names no project can never reach a user: the switch says so.</summary>
    private string PreferenceText(NotificationPreferenceDto preference)
    {
        var label = NotificationLabels.EventLabel(L, preference.EventType);
        return preference.CarriesProjectId ? label : string.Format(L["NotificationPreferenceNoProject"], label);
    }

    private async Task SaveAsync(string eventType, bool isEnabled)
    {
        var saved = await Api.Notifications.SavePreferencesAsync(new UpdateNotificationPreferencesRequest
        {
            Preferences = [new NotificationPreferenceItem { EventType = eventType, IsEnabled = isEnabled }]
        });
        if (saved is null)
        {
            Toast.Error("Error", "SaveFailed");
            return;
        }
        _preferences = saved;
        Toast.Success("Saved", "PreferencesSaved");
    }

    /// <summary>Follows every project the user can read, then lists them.</summary>
    private async Task FollowAllAsync()
    {
        if (_followingAll) return;
        _followingAll = true;
        try
        {
            var before = _subscriptions.Count;
            var subscriptions = await Api.Notifications.FollowAllProjectsAsync();
            if (subscriptions is null)
            {
                Toast.Error("Error", "OperationFailed");
                return;
            }
            _subscriptions = subscriptions;
            Toast.Success("Saved", "NotificationFollowedAllProjects", Math.Max(0, subscriptions.Count - before));
        }
        finally
        {
            _followingAll = false;
        }
    }

    private async Task UnsubscribeAsync(ProjectSubscriptionDto subscription)
    {
        var status = await Api.Notifications.UnsubscribeAsync(subscription.ProjectId);
        if (!status.Success && !status.NotFound)
        {
            Toast.Error("Error", "OperationFailed");
            return;
        }
        _subscriptions = [.. _subscriptions.Where(s => s.ProjectId != subscription.ProjectId)];
        Toast.Success("Saved", "NotificationUnsubscribed");
    }
}
