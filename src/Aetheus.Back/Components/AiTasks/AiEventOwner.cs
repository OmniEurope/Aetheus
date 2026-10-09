// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Back.Components.AiTasks;

/// <summary>
/// The project and server an event is about, read from the top level of its serialized payload
/// (<c>ProjectId</c> and <c>ServerId</c>, as every event emitter names them). An AI task definition
/// belongs to exactly one project or one server, and only an event about that owner may start it:
/// the payload is handed to the task's prompt, so another owner's event would leak its facts.
/// An event carrying neither id starts no definition.
/// </summary>
internal readonly record struct AiEventOwner(int? ProjectId, int? ServerId)
{
    public bool IsKnown => ProjectId.HasValue || ServerId.HasValue;

    public static AiEventOwner Read(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return default;
        return new AiEventOwner(
            ReadId(document.RootElement, "ProjectId"),
            ReadId(document.RootElement, "ServerId"));
    }

    private static int? ReadId(JsonElement payload, string name)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var id))
                return id;
        }
        return null;
    }
}
