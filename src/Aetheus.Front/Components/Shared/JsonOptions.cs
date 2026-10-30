// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Shared JsonSerializerOptions for every API deserialisation in the WASM client.
/// Matches the implicit Web defaults <c>ReadFromJsonAsync</c> uses by default
/// (camelCase, case-insensitive) and layers on the UTC→Local DateTime converters
/// so timestamps appear in the user's browser timezone without per-site fixes.
/// </summary>
internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new UtcToLocalDateTimeConverter(),
            new NullableUtcToLocalDateTimeConverter(),
            new JsonStringEnumConverter()
        }
    };

    /// <summary>
    /// Layers the UTC→Local DateTime converters on the options of a payload that does not come through
    /// <see cref="Web"/>: the SignalR hub protocol. A timestamp pushed over a hub (heartbeat inventory,
    /// task and run events) must read exactly like the same field fetched over HTTP, otherwise a page shows
    /// browser-local time after it loads and UTC once the next push replaces the value. The hub keeps its
    /// own enum handling, so only the date converters are shared.
    /// </summary>
    public static void AddLocalDateTimeConverters(JsonSerializerOptions options)
    {
        options.Converters.Add(new UtcToLocalDateTimeConverter());
        options.Converters.Add(new NullableUtcToLocalDateTimeConverter());
    }
}
