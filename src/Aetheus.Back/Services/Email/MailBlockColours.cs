// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using OmniEurope.Blazor.Components;

namespace Aetheus.Back.Services.Email;

/// <summary>The meaning a status badge, a tile or a banner carries; its colours come from <see cref="MailBlockColours"/>.</summary>
internal enum MailTone
{
    Neutral,
    Success,
    Warning,
    Danger
}

/// <summary>
/// The colours of the fragments Aetheus writes into a mail (status badges, tiles, rows, banners), for the
/// recipient's mode (recette R1-8). A fragment carries literal colours on its elements, so the template's own
/// dark theme cannot recolour it: a light recipient gets the fixed light pairs (dark text on a pale fill of its
/// hue), a dark recipient gets pairs derived from the dark theme of their appearance, the text colour of the
/// theme on its pale-dark fills, or the hue itself where it reads at 4.5:1 or more on its fill.
/// </summary>
internal sealed class MailBlockColours
{
    /// <summary>Text and caption colours of a light mail; <see cref="EmailHtmlLayout"/> reads them from here so the layout depends on the colours, never the other way.</summary>
    internal const string LightInk = "#172b45";
    internal const string LightMuted = "#52647b";

    private const double MinimumContrast = 4.5;

    private readonly Dictionary<MailTone, (string Background, string Ink)> _pairs;

    private MailBlockColours(string ink, string muted, Dictionary<MailTone, (string Background, string Ink)> pairs)
    {
        Ink = ink;
        Muted = muted;
        _pairs = pairs;
    }

    /// <summary>The fixed colours of a light mail, as before dark mail existed.</summary>
    public static MailBlockColours Light { get; } = new(LightInk, LightMuted, new()
    {
        [MailTone.Success] = ("#e3f4e8", "#17643a"),
        [MailTone.Danger] = ("#fde8e6", "#a3231a"),
        [MailTone.Warning] = ("#fff3d6", "#7a4b00"),
        [MailTone.Neutral] = ("#eef0f3", "#404b5a")
    });

    /// <summary>Titles and text of a row.</summary>
    public string Ink { get; }

    /// <summary>Captions, notes and secondary lines.</summary>
    public string Muted { get; }

    public (string Background, string Ink) Pair(MailTone tone) => _pairs[tone];

    /// <summary>The colours of a recipient's theme: the light ones for a light theme, derived ones for a dark theme.</summary>
    public static MailBlockColours For(OmniMailTheme? theme)
    {
        if (theme is not { Dark: true }) return Light;
        return new MailBlockColours(theme.Text, theme.MutedText, new()
        {
            [MailTone.Success] = Dark(theme, theme.SuccessSubtle, theme.Success),
            [MailTone.Danger] = Dark(theme, theme.DangerSubtle, theme.Danger),
            [MailTone.Warning] = Dark(theme, theme.WarningSubtle, theme.Warning),
            [MailTone.Neutral] = (theme.CodeBackground, theme.Text)
        });
    }

    private static (string Background, string Ink) Dark(OmniMailTheme theme, string fill, string hue) =>
        Contrast(fill, hue) >= MinimumContrast ? (fill, hue) : (fill, theme.Text);

    /// <summary>WCAG contrast ratio of two <c>#rgb</c> / <c>#rrggbb</c> colours; 0 when either is another notation (then no hue is trusted).</summary>
    internal static double Contrast(string first, string second)
    {
        if (Luminance(first) is not { } a || Luminance(second) is not { } b) return 0;
        var (light, dark) = a >= b ? (a, b) : (b, a);
        return (light + 0.05) / (dark + 0.05);
    }

    private static double? Luminance(string colour)
    {
        var hex = colour.Trim();
        if (!hex.StartsWith('#')) return null;
        hex = hex[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(digit => new string(digit, 2)));
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return null;
        static double Channel(int value)
        {
            var s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(rgb >> 16 & 0xFF) + 0.7152 * Channel(rgb >> 8 & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
    }
}
