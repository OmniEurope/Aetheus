// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>A question mark whose OE tooltip carries the text; the text is also its accessible name.</summary>
public partial class PipelineInfoTip
{
    [Parameter, EditorRequired] public string Text { get; set; } = string.Empty;
}
