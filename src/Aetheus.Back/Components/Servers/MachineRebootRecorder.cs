// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary>Recette R-11: keeps the last reading of the reboots a machine decides by itself, as its agent reported it. Only
/// a completed reading is kept (<see cref="MachineRebootDataDto.Collected"/>): an agent that did not collect (an older one,
/// the first beat after a start, a host with nothing to read) leaves the stored reading as it was, and a reading equal to the
/// stored one costs no write.</summary>
internal static class MachineRebootRecorder
{
    /// <returns>Whether the stored reading changed and must be saved.</returns>
    public static bool Record(Server server, ServerHeartbeatDto heartbeat)
    {
        if (heartbeat.MachineReboots is not { Collected: true } reported) return false;

        var json = JsonSerializer.Serialize(reported);
        if (string.Equals(json, server.MachineRebootJson, StringComparison.Ordinal)) return false;

        server.MachineRebootJson = json;
        return true;
    }

    /// <summary>The stored reading, or null when none was reported or it is unreadable (the planner then shows nothing).</summary>
    public static MachineRebootDataDto? Read(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<MachineRebootDataDto>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
