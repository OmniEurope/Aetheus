// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Resources;

namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// The recap's words in its configured language, read from DailyRecapStrings.resx (English) and
/// DailyRecapStrings.fr.resx (French). A missing key throws: an e-mail never ships a raw key.
/// </summary>
internal sealed class DailyRecapText(CultureInfo culture)
{
    private static readonly ResourceManager Resources =
        new("Aetheus.Back.Components.DailyRecap.DailyRecapStrings", typeof(DailyRecapText).Assembly);

    public CultureInfo Culture { get; } = culture;

    public string this[string key] =>
        Resources.GetString(key, Culture) ?? throw new InvalidOperationException($"Missing daily recap string '{key}'.");

    public string Format(string key, params object[] args) => string.Format(Culture, this[key], args);

    public string Date(DateOnly day) => day.ToString("D", Culture);

    /// <summary>A UTC instant as the recap's local wall-clock time.</summary>
    public string Time(DateTime utc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone).ToString("t", Culture);

    public string Moment(DateTime utc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone).ToString("g", Culture);

    public string Number(double value) => value.ToString("0.#", Culture);
}
