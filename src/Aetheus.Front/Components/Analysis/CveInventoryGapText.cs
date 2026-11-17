// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Analysis;

/// <summary>Says an inventory's gap codes (<c>code:technology=count, code=count</c>, as the back stores them) in the user's language.</summary>
internal static class CveInventoryGapText
{
    public static IReadOnlyList<string> Describe(string? reason, IStringLocalizer<AppStrings> localizer)
    {
        if (string.IsNullOrWhiteSpace(reason)) return [];
        List<string> lines = [];
        foreach (var part in reason.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.LastIndexOf('=');
            var count = equals > 0 && int.TryParse(part[(equals + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
            var head = equals > 0 ? part[..equals] : part;
            var colon = head.IndexOf(':', StringComparison.Ordinal);
            var code = colon > 0 ? head[..colon] : head;
            var technology = colon > 0 ? head[(colon + 1)..] : null;
            var label = localizer[$"CveInventoryGap_{code}"];
            var text = label.ResourceNotFound ? code : label.Value;
            if (!string.IsNullOrEmpty(technology)) text = $"{text} ({technology})";
            lines.Add(count > 1 ? $"{text} x{count.ToString(CultureInfo.CurrentCulture)}" : text);
        }

        return lines;
    }
}
