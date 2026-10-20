// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Shared;

public partial class ShortId
{
    [Inject] private ClipboardService Clipboard { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public string? Value { get; set; }

    /// <summary>Null: a copy button only when the value was shortened. True or false forces it.</summary>
    [Parameter] public bool? Copyable { get; set; }

    /// <summary>The page of the identified object (commit, release, artifact). The short form
    /// becomes a link to it; null renders plain text, for a value no page is known for.</summary>
    [Parameter] public string? Href { get; set; }

    /// <summary>Extra classes on the outer element, for the host's own layout or typography.</summary>
    [Parameter] public string? Class { get; set; }

    private string CssClass => string.IsNullOrWhiteSpace(Class) ? "short-id" : $"short-id {Class}";

    private string Shortened => Shorten(Value);

    private bool ShowCopy => !string.IsNullOrEmpty(Value) && (Copyable ?? Shortened != Value);

    private Task CopyAsync() => Clipboard.CopyAsync(Value);

    /// <summary> A prefixed (<c>c-</c>, <c>vc-</c>) or bare hexadecimal id of at least 16
    /// characters becomes its prefix and the first eight characters of the id, with no ellipsis and
    /// no suffix, like <see cref="DisplayFormatting.ShortSha"/>. Anything else (a semantic version,
    /// a short name) is returned unchanged. </summary>
    internal static string Shorten(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var match = LongHexId().Match(value);
        if (!match.Success) return value;
        return $"{match.Groups["prefix"].Value}{match.Groups["id"].Value[..8]}";
    }

    [GeneratedRegex("^(?<prefix>(?:[A-Za-z][A-Za-z0-9]*-)?)(?<id>[0-9a-fA-F]{16,}(?:-[0-9a-fA-F]{6,})*)$")]
    private static partial Regex LongHexId();
}
