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
    internal const string Ink = "#172b45";
    internal const string Muted = "#52647b";

    /// <summary>A status badge: its words on a light fill of its hue, never the colour alone.</summary>
    public static StringBuilder Badge(StringBuilder html, string background, string ink, string text, string fontSize = "12px") =>
        html.Append("<span style=\"padding:1px 8px;font-size:").Append(fontSize).Append(";font-weight:600;background-color:")
            .Append(background).Append(";color:").Append(ink).Append("\">").Append(Encode(text)).Append("</span>");

    /// <summary>A row of key figures, one coloured tile each.</summary>
    public static string Tiles(IReadOnlyList<(string Value, string Label, string Background, string Ink)> tiles)
    {
        if (tiles.Count == 0) return string.Empty;
        var width = 100 / tiles.Count;
        var html = new StringBuilder("<table><tr>");
        foreach (var (value, label, background, ink) in tiles)
        {
            html.Append("<td style=\"width:").Append(width).Append("%;padding:12px")
                .Append(";background-color:").Append(background).Append(";color:").Append(ink).Append("\">")
                .Append("<span style=\"font-size:24px;font-weight:700;color:").Append(ink).Append("\">").Append(Encode(value)).Append("</span><br>")
                .Append("<span style=\"font-size:12px;color:").Append(ink).Append("\">").Append(Encode(label)).Append("</span></td>");
        }
        return html.Append("</tr></table>").ToString();
    }

    /// <summary>A section title and its one-line summary.</summary>
    public static StringBuilder Section(StringBuilder html, string title, string summary) =>
        html.Append("<h2 style=\"font-size:17px;color:").Append(Ink).Append("\">").Append(Encode(title)).Append("</h2>")
            .Append("<p style=\"font-size:14px;color:").Append(Muted).Append("\">").Append(Encode(summary)).Append("</p>");

    /// <summary>Opens one row of a list of items.</summary>
    public static StringBuilder ItemRow(StringBuilder html) =>
        html.Append("<tr><td style=\"padding:12px;font-size:15px;color:").Append(Ink).Append("\">");

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
