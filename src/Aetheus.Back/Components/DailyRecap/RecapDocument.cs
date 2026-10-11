// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// The recap laid out once, in words, before it becomes HTML or plain text: both renderings read
/// this same document, so the two parts of the e-mail can never tell different stories.
/// </summary>
internal sealed record RecapDocument(
    string Subject,
    string Heading,
    string Intro,
    IReadOnlyList<RecapTile> Tiles,
    IReadOnlyList<RecapBlock> Blocks,
    string Footer,
    string LabelSeparator,
    RecapLink? SettingsLink = null);

/// <summary>A link the reader follows, such as the settings page of the recap (recette R1-40).</summary>
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

/// <summary>One section: a title, a one-line summary, its items, and an optional closing note.</summary>
internal sealed record RecapBlock(string Title, string Summary, IReadOnlyList<RecapItem> Items, string? Note = null);

/// <summary>One thing the section reports: a run, a server, a certificate.</summary>
internal sealed record RecapItem(string Title, string? Subtitle, RecapBadge? Badge, IReadOnlyList<RecapLine> Lines);

/// <summary>A labelled detail of an item, such as "Disk: [Low space] 4 GB free of 40 GB".</summary>
internal sealed record RecapLine(string Label, RecapBadge? Badge, string Text);
