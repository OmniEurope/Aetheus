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

    /// <summary>
    /// A string that agrees with its count: the key itself for a plural, the key suffixed with "One" for a
    /// singular (one in English; zero and one in French, as in the French grammar). A singular missing from the
    /// resources falls back to the plain key, for the words that do not change with the number.
    /// </summary>
    public string Plural(string key, double count, params object[] args)
    {
        var singular = IsSingular(count) ? Resources.GetString(key + "One", Culture) : null;
        return string.Format(Culture, singular ?? this[key], args);
    }

    /// <summary>French reads 0 and 1 (and any fraction below 2) as singular; English reads only exactly 1.</summary>
    public bool IsSingular(double count) =>
        Culture.TwoLetterISOLanguageName == "fr" ? count >= 0 && count < 2 : count == 1;

    public string Date(DateOnly day) => day.ToString("D", Culture);

    /// <summary>A UTC instant as the recap's local wall-clock time.</summary>
    public string Time(DateTime utc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone).ToString("t", Culture);

    public string Moment(DateTime utc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone).ToString("g", Culture);

    public string Number(double value) => value.ToString("0.#", Culture);
}
