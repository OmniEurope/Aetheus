// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Git;

/// <summary>Decision D13: the three branch protection levels, in order, with their localized labels,
/// for the rule dialog and the rule grid.</summary>
public static class BranchProtectionLevelOptions
{
    public static List<OmniOption<BranchProtectionLevel>> Build(IStringLocalizer localizer) =>
        Enum.GetValues<BranchProtectionLevel>()
            .Select(level => new OmniOption<BranchProtectionLevel>(level, localizer.Localize(level)))
            .ToList();
}
