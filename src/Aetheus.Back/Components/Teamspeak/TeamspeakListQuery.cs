// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Teamspeak;

/// <summary> The column filters of a server's TeamSpeak grids (clients, channels, bans). The keys
/// are the grids' column keys. </summary>
internal static class TeamspeakListQuery
{
    internal static readonly GridQueryMap<TeamspeakClient> ClientColumns = new GridQueryMap<TeamspeakClient>()
        .Text("nickname", c => c.Nickname)
        .Text("channelName", c => c.ChannelName)
        .Text("platform", c => c.Platform);

    internal static readonly GridQueryMap<TeamspeakChannel> ChannelColumns = new GridQueryMap<TeamspeakChannel>()
        .Text("name", c => c.Name);

    internal static readonly GridQueryMap<TeamspeakBan> BanColumns = new GridQueryMap<TeamspeakBan>()
        .Number("banId", b => b.BanId)
        .Text("nickname", b => b.Nickname)
        .Text("uniqueId", b => b.UniqueId)
        .Text("reason", b => b.Reason);
}
