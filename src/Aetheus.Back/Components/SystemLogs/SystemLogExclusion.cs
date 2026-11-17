// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.SystemLogs;

/// <summary>Decides which tab of the system logs page an entry belongs to (R-28): the second one, when an
/// enabled rule of the organization matches it. Pure, so the logs query and its tests share one definition.</summary>
internal static class SystemLogExclusion
{
    /// <summary>True when the rule is on and the entry's message contains its pattern and its category
    /// contains the rule's category (an empty category matches every category), case-insensitive.</summary>
    internal static bool Matches(SystemLogEntryDto entry, SystemLogExclusionRuleDto rule)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(rule);
        if (!rule.Enabled || string.IsNullOrEmpty(rule.Pattern)) return false;
        if (!entry.Message.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase)) return false;
        return rule.Category.Length == 0
            || entry.Category.Contains(rule.Category, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool MatchesAny(SystemLogEntryDto entry, IReadOnlyList<SystemLogExclusionRuleDto> rules)
    {
        foreach (var rule in rules)
        {
            if (Matches(entry, rule)) return true;
        }

        return false;
    }
}
