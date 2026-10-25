// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Hubs;

/// <summary>
/// Targets a single user via <c>Clients.User(userId)</c>. The default SignalR IUserIdProvider maps
/// to the JWT <c>NameIdentifier</c> claim, which equals the user id (AuthService.BuildUserClaims),
/// so no extra wiring is required.
/// </summary>
public sealed class UserChangeNotifier(IHubContext<UserHub> hub, HubConnectionRegistry connections) : IUserChangeNotifier
{
    /// <summary>
    /// Also aborts the user's other hub connections: the groups they joined were authorized against the
    /// old rights, and the client's reconnect re-joins them against the new ones (SITE-SEC-002).
    /// </summary>
    public async Task NotifyPermissionsChangedAsync(int userId, string reason, CancellationToken ct = default)
    {
        var id = userId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await hub.Clients.User(id).SendAsync("PermissionsChanged", reason, ct).ConfigureAwait(false);
        connections.AbortUser(id);
    }

    public Task NotifyNotificationsChangedAsync(IReadOnlyCollection<int> userIds, CancellationToken ct = default) =>
        userIds.Count == 0
            ? Task.CompletedTask
            : hub.Clients.Users(userIds.Distinct().Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList())
                .SendAsync("NotificationsChanged", ct);
}
