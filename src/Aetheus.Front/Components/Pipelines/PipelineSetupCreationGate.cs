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

    /// <summary>True while the creation is held back by something the wizard waits for or could not read: the push the user
    /// accepted to wait for (R-20), or the environment sample and Apache templates of the production deployment (R-7), whose
    /// absence from the setup would leave <c>PORT_FRONT</c> and <c>PORT_BACK</c> undefined. <paramref name="production"/> is
    /// null when the production pipeline is not selected.</summary>
    public static bool IsHeld(PipelineSetupFirstPush? firstPush, PipelineSetupProduction? production) =>
        firstPush is { Waiting: true } || production is { TemplatesUnreadable: true } || production is { ServerMissing: true };
}
