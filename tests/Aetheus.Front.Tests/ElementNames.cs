// SPDX-License-Identifier: EUPL-1.2

using AngleSharp.Dom;

namespace Aetheus.Front.Tests;

/// <summary>Find icon-only row actions by their title and aria-label as well as visible text, so
/// tests identify the action independently of markup.</summary>
internal static class ElementNames
{
    /// <summary>Visible text plus the accessible names of the element, joined for a substring search.</summary>
    internal static string Names(this IElement element) =>
        $"{element.TextContent} {element.GetAttribute("title")} {element.GetAttribute("aria-label")}";
}
