// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Portsentry;

/// <summary> The column filters of a server's PortSentry grids (blocked IPs, whitelist). The keys
/// are the grids' column keys. </summary>
internal static class PortsentryListQuery
{
    internal static readonly GridQueryMap<PortsentryBlockedIp> BlockedColumns = new GridQueryMap<PortsentryBlockedIp>()
        .Text("ipAddress", b => b.IpAddress)
        .Text("protocol", b => b.Protocol)
        .Date("blockedAt", b => b.BlockedAt)
        .Text("reason", b => b.Reason);

    internal static readonly GridQueryMap<PortsentryWhitelistIp> WhitelistColumns = new GridQueryMap<PortsentryWhitelistIp>()
        .Text("ipAddress", w => w.IpAddress)
        .Text("description", w => w.Description)
        .Date("createdAt", w => w.CreatedAt);
}
