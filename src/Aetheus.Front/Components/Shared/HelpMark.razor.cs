// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

public partial class HelpMark
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>What the line counts, in one or two sentences; also the mark's accessible description.</summary>
    [Parameter, EditorRequired] public string Text { get; set; } = string.Empty;

    [Parameter] public string? TestId { get; set; }
}
