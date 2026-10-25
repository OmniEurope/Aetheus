// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>A library or vault the summary lists: still to create, or created from the summary.</summary>
internal sealed record RecapElement(bool IsVault, string Name, bool Created);

/// <summary> What the summary of the pipeline setup must have in place before the pipelines are
/// created: a Git repository, and the libraries and vaults the chosen templates read. Read from the
/// readiness verdict, so the wizard's final action and the summary agree on the same answer.
/// </summary>
internal static class PipelineSetupCreationGate
{
    public static bool RepositoryMissing(PipelineSetupReadinessDto? readiness) =>
        readiness?.Checks.Any(check => check.Kind == PipelineSetupReadinessKind.NoRepository) == true;

    /// <summary>The libraries and vaults the verdict reports missing.</summary>
    public static IReadOnlyList<RecapElement> MissingElements(PipelineSetupReadinessDto? readiness) =>
        readiness is null
            ? []
            : [.. readiness.Checks
                .Where(check => check.Kind is PipelineSetupReadinessKind.MissingRequiredLibraries
                    or PipelineSetupReadinessKind.MissingRequiredVaults)
                .SelectMany(check => check.Items.Select(item =>
                    new RecapElement(check.Kind == PipelineSetupReadinessKind.MissingRequiredVaults, item, false)))];

    /// <summary>True while the repository or a required library or vault is still to create.</summary>
    public static bool IsBlocked(PipelineSetupReadinessDto? readiness) =>
        RepositoryMissing(readiness) || MissingElements(readiness).Count > 0;
}
