// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The content of a deploy run's Schema tab (recette R-18). The page shows the tab only for a run with a gate record.</summary>
public partial class PipelineRunSchemaTab
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineRunSchemaDto Schema { get; set; } = default!;

    /// <summary>A contract is the rarer, riskier kind, so it is the one that is not green.</summary>
    internal static OmniTone KindTone(SchemaMigrationKind kind) =>
        kind == SchemaMigrationKind.Contract ? OmniTone.Warning : OmniTone.Info;
}
