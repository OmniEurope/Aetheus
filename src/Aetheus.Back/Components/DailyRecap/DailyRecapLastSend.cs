// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Back.Components.DailyRecap;

/// <summary>What the last scheduled recap became, kept as JSON in <see cref="DailyRecapSettingKeys.LastResult"/>.</summary>
internal sealed record DailyRecapLastSend(DateTime SentAtUtc, int Recipients, int Delivered, string? Error)
{
    public string Serialize() => JsonSerializer.Serialize(this);

    public static DailyRecapLastSend? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<DailyRecapLastSend>(json);
        }
        catch (JsonException)
        {
            // Mandatory: an unreadable row only loses the report of the last send, never the settings page.
            return null;
        }
    }
}
