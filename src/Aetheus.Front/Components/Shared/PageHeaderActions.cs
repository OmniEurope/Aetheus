// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>The page header section that list components fill with their actions, so every host (page,
/// project or server detail layout) places them the same way without a separate toolbar.</summary>
internal static class PageHeaderActions
{
    /// <summary>Name of the section a host renders in its page header and a list fills.</summary>
    internal const string SectionName = "pipelines-header-actions";

    /// <summary>Name of the section a host renders in the status badges of its page header (right after the
    /// title), for a section whose state belongs on the title line (R-18).</summary>
    internal const string BadgesSectionName = "page-header-badges";
}
