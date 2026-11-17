// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Analysis;

/// <summary>R-15 and R-22: how fresh each source of the vulnerability watch is, as the server reads it now.</summary>
public partial class CveWatchSourcesCard : ComponentBase
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public IReadOnlyList<CveWatchSourceDto> Sources { get; set; } = [];

    /// <summary>True while the sources are still being read: an empty list is then no conclusion, and the card draws no
    /// "no source" sentence.</summary>
    [Parameter] public bool Pending { get; set; }

    /// <summary>The heading; the usual one when null.</summary>
    [Parameter] public string? Title { get; set; }
}
