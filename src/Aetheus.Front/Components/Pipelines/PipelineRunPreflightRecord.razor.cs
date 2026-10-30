// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The preflight record kept on a run. Its own component so the run page stays within the file-size
/// budget (FileSizeAuditTests) and so the card can be shown wherever a run is inspected.
/// </summary>
public partial class PipelineRunPreflightRecord
{
    /// <summary>The requirement classes the back records (<c>PreflightCheckKinds</c> and the kinds of
    /// <c>PipelineRequirementsChecker</c>); each has a <c>PreflightKind_{kind}</c> resource.</summary>
    internal static readonly HashSet<string> KnownKinds = new(StringComparer.Ordinal)
    {
        "runner", "environment", "pipeline-reference", "ports", "child-pipeline", "scanner-manifest",
        "release-artifact", "ai-profile", "library", "vault", "capability"
    };

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public IReadOnlyList<PreflightCheckDto> Checks { get; set; } = [];

    /// <summary>The class of requirement in French; a kind this front does not know (a newer back, an older
    /// run) is shown as recorded rather than hidden.</summary>
    private string KindText(string kind) => KnownKinds.Contains(kind) ? L[$"PreflightKind_{kind}"].Value : kind;
}
