// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Notifications;

/// <summary>Read the follow state once per project in the header and share it with the menu.
/// Knowing the state before opening keeps the action label and position stable.</summary>
public sealed class ProjectSubscriptionState(ApiClient api, NotifyHelper toast)
{
    private int _requestedFor;

    /// <summary>The project the state describes; 0 before the first load.</summary>
    public int ProjectId { get; private set; }

    /// <summary>Null while unknown (loading, or the read failed): the entry stays hidden rather than
    /// showing a label that may be wrong.</summary>
    public bool? Subscribed { get; private set; }

    public bool Busy { get; private set; }

    /// <summary>Reads the state of <paramref name="projectId"/>, once: a second call for the same
    /// project changes nothing. Returns whether the state changed.</summary>
    public async Task<bool> LoadAsync(int projectId)
    {
        if (projectId <= 0 || _requestedFor == projectId) return false;
        _requestedFor = projectId;
        ProjectId = projectId;
        Subscribed = null;
        try
        {
            var subscriptions = await api.Notifications.GetSubscriptionsAsync();
            if (_requestedFor != projectId) return false;
            Subscribed = subscriptions.Any(subscription => subscription.ProjectId == projectId);
        }
        catch (HttpRequestException)
        {
            // Unknown state: the entry stays hidden rather than guessing.
        }

        return true;
    }

    public async Task ToggleAsync()
    {
        if (Subscribed is not { } subscribed || Busy) return;
        var projectId = ProjectId;
        Busy = true;
        try
        {
            if (subscribed)
            {
                var status = await api.Notifications.UnsubscribeAsync(projectId);
                if (status.Success || status.NotFound)
                {
                    if (ProjectId == projectId) Subscribed = false;
                    toast.Success("Saved", "NotificationUnsubscribed");
                }
                else
                {
                    toast.Error("Error", "OperationFailed");
                }
            }
            else if (await api.Notifications.SubscribeAsync(projectId) is not null)
            {
                if (ProjectId == projectId) Subscribed = true;
                toast.Success("Saved", "NotificationSubscribed");
            }
            else
            {
                toast.Error("Error", "OperationFailed");
            }
        }
        finally
        {
            Busy = false;
        }
    }
}
