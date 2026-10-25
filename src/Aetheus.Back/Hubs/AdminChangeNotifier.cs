// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Hubs;

/// <summary>
/// Pushes <c>AdminEntityChanged(entity, id, op)</c> over <see cref="AdminHub"/> to every connected
/// client (the admin list pages that opened the hub). Reuses <see cref="EntityChangeOps"/> for the op.
/// </summary>
public sealed class AdminChangeNotifier(IHubContext<AdminHub> hub) : IAdminChangeNotifier
{
    public Task BroadcastAsync(string entity, int id, string op, CancellationToken ct = default)
        => hub.Clients.All.SendAsync("AdminEntityChanged", entity, id, op, ct);
}
