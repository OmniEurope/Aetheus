// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Reads UTC DateTimes from the wire (the backend timestamps everything in UTC,
/// per the CLAUDE.md TimeProvider convention) and surfaces them in the browser's
/// LOCAL timezone, so DataGrid / ToString("g") show correct local clock time
/// without each call site having to remember ToLocalTime(). Inverse on write:
/// Local values are sent as UTC so round-trips stay UTC-canonical on the wire.
/// <see cref="DateTime.MinValue"/> and <see cref="DateTime.MaxValue"/> are sentinels
/// ("never", "no limit"), not instants: they cross the converter unshifted in both
/// directions, so a <c>== default</c> guard on a deserialized DTO keeps working.
/// </summary>
internal sealed class UtcToLocalDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ToLocal(reader.GetDateTime());

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToUtc(value));

    /// <summary>Wire value to browser-local time; the MinValue/MaxValue sentinels are kept as is.</summary>
    internal static DateTime ToLocal(DateTime dt)
    {
        if (IsSentinel(dt)) return dt;
        return dt.Kind switch
        {
            DateTimeKind.Utc => dt.ToLocalTime(),
            // No 'Z' on the wire - treat as UTC per backend convention, then localise.
            DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime(),
            _ => dt
        };
    }

    /// <summary>Front value to UTC for the wire; the MinValue/MaxValue sentinels are sent unshifted as UTC.</summary>
    internal static DateTime ToUtc(DateTime value)
    {
        if (IsSentinel(value)) return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    // DateTime equality compares ticks only, so the Kind carried by the wire value does not matter.
    private static bool IsSentinel(DateTime value) => value == DateTime.MinValue || value == DateTime.MaxValue;
}

internal sealed class NullableUtcToLocalDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        return UtcToLocalDateTimeConverter.ToLocal(reader.GetDateTime());
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStringValue(UtcToLocalDateTimeConverter.ToUtc(value.Value));
    }
}
