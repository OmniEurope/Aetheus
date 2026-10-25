// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services;

public interface IUserChangeNotifier
{
    /// <summary>
    /// Pushes <c>PermissionsChanged(reason)</c> to every connection of the given user over the user hub.
    /// Called after the change is persisted and the authz cache invalidated.
    /// </summary>
    Task NotifyPermissionsChangedAsync(int userId, string reason, CancellationToken ct = default);

    /// <summary> Pushes <c>NotificationsChanged</c> to every connection of the given users, so
    /// their bell, menu badge and /notifications page reload without waiting for the poll.
    /// </summary>
    Task NotifyNotificationsChangedAsync(IReadOnlyCollection<int> userIds, CancellationToken ct = default);
}
