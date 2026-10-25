// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// The recap laid out once, in words, before its template (recette R1-75) renders it: the HTML and
/// the plain text are both written from that one rendering, so they can never tell different stories.
/// </summary>
internal sealed record RecapDocument(
    string Subject,
    string Heading,
    IReadOnlyList<RecapTile> Tiles,
    IReadOnlyList<RecapBlock> Blocks,
    string Footer,
    string LabelSeparator,
    RecapLink? SettingsLink = null,
    string Label = "",
    string Date = "");

/// <summary>A link the reader follows, such as the settings page of the recap.</summary>
internal sealed record RecapLink(string Text, string Url);

/// <summary>A status colour; a badge always carries its words too, never the colour alone.</summary>
internal enum RecapTone
{
    Success,
    Warning,
    Danger,
    Neutral
}

internal sealed record RecapBadge(RecapTone Tone, string Text);

/// <summary>A key figure at the top of the e-mail.</summary>
internal sealed record RecapTile(string Label, string Value, RecapTone Tone);

/// <summary>One section: a title, a one-line summary, its items, an optional closing note and an optional link for the whole section.</summary>
internal sealed record RecapBlock(string Title, string Summary, IReadOnlyList<RecapItem> Items, string? Note = null, RecapLink? Link = null);

/// <summary>One thing the section reports: a run, a server, a certificate. With <paramref name="TitleIsLink"/> the title itself opens the link, with no separate link line.</summary>
internal sealed record RecapItem(string Title, string? Subtitle, RecapBadge? Badge, IReadOnlyList<RecapLine> Lines, RecapLink? Link = null, bool TitleIsLink = false);

/// <summary>A labelled detail of an item, such as "Disk: [Low space] 4 Go free of 40 Go".</summary>
internal sealed record RecapLine(string Label, RecapBadge? Badge, string Text);
