// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// The HTML part of the recap, written for mail clients: nested tables, every style inline (no
/// style element, which many clients strip), a 640-pixel column that shrinks to the phone's width,
/// a light background, and status badges that carry their words as well as their colour. Lines
/// between rows only, never a coloured band down one side of a block.
/// </summary>
internal static class DailyRecapHtmlRenderer
{
    private const string Font = "font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;";
    private const string PageBackground = "#f3f4f6";
    private const string Ink = "#1f2933";
    private const string Muted = "#5b6675";
    private const string Rule = "#e4e7eb";

    public static string Render(RecapDocument document, string languageTag)
    {
        ArgumentNullException.ThrowIfNull(document);
        var html = new StringBuilder(16 * 1024);
        html.Append("<!DOCTYPE html><html lang=\"").Append(Encode(languageTag)).Append("\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<meta name=\"color-scheme\" content=\"light\">")
            .Append("<title>").Append(Encode(document.Subject)).Append("</title></head>")
            .Append("<body style=\"margin:0;padding:0;background:").Append(PageBackground).Append(";\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"background:")
            .Append(PageBackground).Append(";\"><tr><td align=\"center\" style=\"padding:24px 12px;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"max-width:640px;width:100%;background:#ffffff;border:1px solid ")
            .Append(Rule).Append(";border-radius:8px;\">");

        Header(html, document);
        if (document.Tiles.Count > 0) Tiles(html, document.Tiles);
        foreach (var block in document.Blocks) Block(html, block, document);

        html.Append("<tr><td style=\"padding:16px 24px 24px;").Append(Font).Append("font-size:12px;line-height:18px;color:")
            .Append(Muted).Append(";border-top:1px solid ").Append(Rule).Append(";\">").Append(Encode(document.Footer));
        if (document.SettingsLink is { } link)
            html.Append("<br><a href=\"").Append(Encode(link.Url)).Append("\" style=\"color:").Append(Ink).Append(";\">")
                .Append(Encode(link.Text)).Append("</a>");
        html.Append("</td></tr>")
            .Append("</table></td></tr></table></body></html>");
        return html.ToString();
    }

    private static void Header(StringBuilder html, RecapDocument document) =>
        html.Append("<tr><td style=\"padding:24px 24px 8px;").Append(Font).Append("\">")
            .Append("<div style=\"font-size:12px;letter-spacing:1px;text-transform:uppercase;color:").Append(Muted).Append(";\">Aetheus</div>")
            .Append("<h1 style=\"margin:4px 0 8px;font-size:22px;line-height:28px;color:").Append(Ink).Append(";\">")
            .Append(Encode(document.Heading)).Append("</h1>")
            .Append("<p style=\"margin:0;font-size:14px;line-height:20px;color:").Append(Muted).Append(";\">")
            .Append(Encode(document.Intro)).Append("</p></td></tr>");

    private static void Tiles(StringBuilder html, IReadOnlyList<RecapTile> tiles)
    {
        var width = 100 / tiles.Count;
        html.Append("<tr><td style=\"padding:8px 18px 4px;\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\"><tr>");
        foreach (var tile in tiles)
        {
            var (background, ink) = Colours(tile.Tone);
            html.Append("<td width=\"").Append(width).Append("%\" valign=\"top\" style=\"padding:6px;\">")
                .Append("<div style=\"background:").Append(background).Append(";border-radius:6px;padding:12px;").Append(Font).Append("\">")
                .Append("<div style=\"font-size:24px;line-height:28px;font-weight:700;color:").Append(ink).Append(";\">").Append(Encode(tile.Value)).Append("</div>")
                .Append("<div style=\"font-size:12px;line-height:16px;color:").Append(ink).Append(";\">").Append(Encode(tile.Label)).Append("</div>")
                .Append("</div></td>");
        }
        html.Append("</tr></table></td></tr>");
    }

    private static void Block(StringBuilder html, RecapBlock block, RecapDocument document)
    {
        html.Append("<tr><td style=\"padding:20px 24px 4px;").Append(Font).Append("\">")
            .Append("<h2 style=\"margin:0 0 4px;font-size:17px;line-height:24px;color:").Append(Ink).Append(";\">").Append(Encode(block.Title)).Append("</h2>")
            .Append("<p style=\"margin:0 0 8px;font-size:14px;line-height:20px;color:").Append(Muted).Append(";\">").Append(Encode(block.Summary)).Append("</p>");
        if (block.Items.Count > 0)
        {
            html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\">");
            foreach (var item in block.Items) Item(html, item, document);
            html.Append("</table>");
        }
        if (!string.IsNullOrWhiteSpace(block.Note))
            html.Append("<p style=\"margin:8px 0 0;font-size:12px;line-height:18px;color:").Append(Muted).Append(";\">").Append(Encode(block.Note)).Append("</p>");
        html.Append("</td></tr>");
    }

    private static void Item(StringBuilder html, RecapItem item, RecapDocument document)
    {
        html.Append("<tr><td style=\"padding:10px 0;border-top:1px solid ").Append(Rule).Append(";").Append(Font).Append("\">")
            .Append("<div style=\"font-size:15px;line-height:22px;color:").Append(Ink).Append(";\">");
        if (item.Badge is { } badge) Badge(html, badge).Append(' ');
        html.Append("<strong>").Append(Encode(item.Title)).Append("</strong></div>");
        if (!string.IsNullOrWhiteSpace(item.Subtitle))
            html.Append("<div style=\"font-size:13px;line-height:18px;color:").Append(Muted).Append(";\">").Append(Encode(item.Subtitle)).Append("</div>");
        foreach (var line in item.Lines)
        {
            html.Append("<div style=\"font-size:13px;line-height:22px;color:").Append(Ink).Append(";\">")
                .Append("<span style=\"color:").Append(Muted).Append(";\">").Append(Encode(line.Label + document.LabelSeparator)).Append("</span> ");
            if (line.Badge is { } lineBadge) Badge(html, lineBadge).Append(' ');
            html.Append(Encode(line.Text)).Append("</div>");
        }
        html.Append("</td></tr>");
    }

    private static StringBuilder Badge(StringBuilder html, RecapBadge badge)
    {
        var (background, ink) = Colours(badge.Tone);
        return html.Append("<span style=\"display:inline-block;padding:1px 8px;border-radius:10px;font-size:12px;line-height:18px;font-weight:600;background:")
            .Append(background).Append(";color:").Append(ink).Append(";\">").Append(Encode(badge.Text)).Append("</span>");
    }

    /// <summary>Light fill, dark text of the same hue: readable, and AA contrast on every pair.</summary>
    internal static (string Background, string Ink) Colours(RecapTone tone) => tone switch
    {
        RecapTone.Success => ("#e3f4e8", "#17643a"),
        RecapTone.Danger => ("#fde8e6", "#a3231a"),
        RecapTone.Warning => ("#fff3d6", "#7a4b00"),
        _ => ("#eef0f3", "#404b5a")
    };

    /// <summary>Escapes the markup characters only (every attribute is double-quoted): accented letters stay readable in a UTF-8 e-mail.</summary>
    internal static string Encode(string? value) => (value ?? string.Empty)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
