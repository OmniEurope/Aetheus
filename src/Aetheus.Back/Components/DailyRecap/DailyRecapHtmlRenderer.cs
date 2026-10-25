// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Services.Email;
using static Aetheus.Back.Services.Email.EmailHtmlLayout;

namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// What the daily recap template (recette R1-75) is rendered with: the day, the period and the settings
/// link as values, the tiles and the sections as blocks (HTML fragments the template places). The mail
/// itself, its frame, title and footer, is the template's. Status badges carry their words as well as their
/// colour; lines between rows only, never a coloured band down one side of a block.
/// </summary>
internal static class DailyRecapHtmlRenderer
{
    /// <param name="colours">The colours of the fragments for the recipient's mode (recette R1-8); light by default.</param>
    public static MailTemplateData Data(RecapDocument document, MailBlockColours? colours = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        colours ??= MailBlockColours.Light;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DailyRecapMailVariables.Date] = document.Date,
            [DailyRecapMailVariables.Period] = string.Empty,
            [DailyRecapMailVariables.SettingsUrl] = document.SettingsLink?.Url ?? string.Empty
        };
        var blocks = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [DailyRecapMailVariables.Tiles] = EmailHtmlLayout.Tiles(
                [.. document.Tiles.Select(tile => (tile.Value, tile.Label, Tone(colours, tile.Tone).Background, Tone(colours, tile.Tone).Ink))]),
            [DailyRecapMailVariables.Sections] = Sections(document, colours)
        };
        return new MailTemplateData(values, blocks);
    }

    private static string Sections(RecapDocument document, MailBlockColours colours)
    {
        var html = new StringBuilder(8 * 1024);
        foreach (var block in document.Blocks)
        {
            Section(html, block.Title, block.Summary, colours);
            if (block.Items.Count > 0)
            {
                html.Append("<table>");
                foreach (var item in block.Items) Item(html, item, document, colours);
                html.Append("</table>");
            }
            if (block.Link is { } blockLink)
                html.Append("<p><a href=\"").Append(Encode(blockLink.Url)).Append("\" style=\"font-size:13px;font-weight:600\">")
                    .Append(Encode(blockLink.Text)).Append("</a></p>");
            if (!string.IsNullOrWhiteSpace(block.Note))
                html.Append("<p style=\"font-size:12px;color:").Append(colours.Muted).Append("\">").Append(Encode(block.Note)).Append("</p>");
        }
        return html.ToString();
    }

    private static void Item(StringBuilder html, RecapItem item, RecapDocument document, MailBlockColours colours)
    {
        ItemRow(html, colours);
        if (item.Badge is { } badge) Badge(html, badge, colours).Append(' ');
        var titleLink = item.TitleIsLink ? item.Link : null;
        if (titleLink is not null)
            html.Append("<a href=\"").Append(Encode(titleLink.Url)).Append("\"><strong>").Append(Encode(item.Title)).Append("</strong></a>");
        else
            html.Append("<strong>").Append(Encode(item.Title)).Append("</strong>");
        if (!string.IsNullOrWhiteSpace(item.Subtitle))
            html.Append("<br><span style=\"font-size:13px;color:").Append(colours.Muted).Append("\">").Append(Encode(item.Subtitle)).Append("</span>");
        foreach (var line in item.Lines)
        {
            html.Append("<br><span style=\"font-size:13px;color:").Append(colours.Muted).Append("\">")
                .Append(Encode(line.Label + document.LabelSeparator)).Append("</span> ");
            if (line.Badge is { } lineBadge) Badge(html, lineBadge, colours).Append(' ');
            html.Append("<span style=\"font-size:13px;color:").Append(colours.Ink).Append("\">").Append(Encode(line.Text)).Append("</span>");
        }
        if (!item.TitleIsLink && item.Link is { } link)
            html.Append("<br><a href=\"").Append(Encode(link.Url)).Append("\" style=\"font-size:13px;font-weight:600\">")
                .Append(Encode(link.Text)).Append("</a>");
        html.Append("</td></tr>");
    }

    private static StringBuilder Badge(StringBuilder html, RecapBadge badge, MailBlockColours colours)
    {
        var (background, ink) = Tone(colours, badge.Tone);
        return EmailHtmlLayout.Badge(html, background, ink, badge.Text);
    }

    /// <summary>The fill and text of a tone for the recipient's mode: light fill and dark text of the same hue on a light mail.</summary>
    internal static (string Background, string Ink) Tone(MailBlockColours colours, RecapTone tone) => colours.Pair(tone switch
    {
        RecapTone.Success => MailTone.Success,
        RecapTone.Danger => MailTone.Danger,
        RecapTone.Warning => MailTone.Warning,
        _ => MailTone.Neutral
    });
}
