// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Back.Services.Email;

/// <summary>
/// The HTML fragments the notification and daily recap mails give their templates as block variables
/// (recette R1-75): OmniMailRenderer lays out the mail, these write the parts only Aetheus knows (status
/// badges, tiles, rows). Each look is written on its element with the declarations the mail's block
/// allow-list keeps: colour, background colour, size, weight, padding, width. No border: OmniEurope.Blazor
/// 1.8.2 drops every border declaration of a fragment (measured), so each cell keeps the thin rule the mail
/// draws around table cells, and a banner is a padded block rather than a cell.
/// </summary>
internal static class EmailHtmlLayout
{
    internal const string Ink = MailBlockColours.LightInk;
    internal const string Muted = MailBlockColours.LightMuted;

    /// <summary>A status badge: its words on a light fill of its hue, never the colour alone.</summary>
    public static StringBuilder Badge(StringBuilder html, string background, string ink, string text, string fontSize = "12px") =>
        html.Append("<span style=\"padding:1px 8px;font-size:").Append(fontSize).Append(";font-weight:600;background-color:")
            .Append(background).Append(";color:").Append(ink).Append("\">").Append(Encode(text)).Append("</span>");

    /// <summary>
    /// The key figures, one coloured tile each, stacked with a gap between them (recette R-57): a row of
    /// cells split a figure such as "1 / 1" over two lines on a phone, and a fragment cannot carry a media
    /// query to stack them only there. The figure and its words share one line, which wraps as text.
    /// </summary>
    public static string Tiles(IReadOnlyList<(string Value, string Label, string Background, string Ink)> tiles)
    {
        if (tiles.Count == 0) return string.Empty;
        // One table per tile: the mail's own table margin is the gap between them.
        var html = new StringBuilder();
        foreach (var (value, label, background, ink) in tiles)
        {
            html.Append("<table><tr><td style=\"padding:12px;background-color:").Append(background).Append(";color:").Append(ink).Append("\">")
                .Append("<span style=\"font-size:22px;font-weight:700;color:").Append(ink).Append("\">").Append(Encode(value).Replace(" ", "&nbsp;")).Append("</span> ")
                .Append("<span style=\"font-size:14px;color:").Append(ink).Append("\">").Append(Encode(label)).Append("</span></td></tr></table>");
        }
        return html.ToString();
    }

    /// <summary>A section title and its one-line summary.</summary>
    public static StringBuilder Section(StringBuilder html, string title, string summary, MailBlockColours? colours = null) =>
        html.Append("<h2 style=\"font-size:17px;color:").Append((colours ?? MailBlockColours.Light).Ink).Append("\">").Append(Encode(title)).Append("</h2>")
            .Append("<p style=\"font-size:14px;color:").Append((colours ?? MailBlockColours.Light).Muted).Append("\">").Append(Encode(summary)).Append("</p>");

    /// <summary>Opens one row of a list of items.</summary>
    public static StringBuilder ItemRow(StringBuilder html, MailBlockColours? colours = null) =>
        html.Append("<tr><td style=\"padding:12px;font-size:15px;color:").Append((colours ?? MailBlockColours.Light).Ink).Append("\">");

    /// <summary>Only the configured HTTP(S) origin and a fixed application route become links.</summary>
    public static string? Url(string? frontOrigin, string path) =>
        Uri.TryCreate(frontOrigin?.Trim(), UriKind.Absolute, out var origin)
        && (origin.Scheme == Uri.UriSchemeHttps || origin.Scheme == Uri.UriSchemeHttp)
        && origin.UserInfo.Length == 0
            ? origin.GetLeftPart(UriPartial.Authority) + path : null;

    public static string Encode(string? value) => (value ?? string.Empty)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
