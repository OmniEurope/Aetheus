// SPDX-License-Identifier: EUPL-1.2

using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary>
/// The filesystems of a server as the last heartbeat reported them, kept on the server row so the
/// storage section can draw each one. Only the distinct storage is kept (a bind mount of a device
/// already listed is the same space, see <see cref="DiskInfoDto.Distinct"/>).
/// </summary>
internal static class ServerDisks
{
    /// <summary>Stores the heartbeat's filesystems; the column is untouched when nothing changed.</summary>
    public static void Apply(Server server, ServerHeartbeatDto heartbeat)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(heartbeat);
        var json = heartbeat.Disks.Count == 0
            ? null
            : JsonSerializer.Serialize(DiskInfoDto.Distinct(heartbeat.Disks));
        if (server.DisksJson != json) server.DisksJson = json;
    }

    /// <summary>The stored filesystems; empty when the agent never reported any or the column is unreadable.</summary>
    public static List<DiskInfoDto> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<DiskInfoDto>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
