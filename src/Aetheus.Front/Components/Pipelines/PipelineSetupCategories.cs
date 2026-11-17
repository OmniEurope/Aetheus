// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>One category of the wizard's capabilities step: its name in the wizard and the capabilities (the option names of the
/// wizard's address) that live in it, in the order their tiles are drawn.</summary>
internal sealed record PipelineSetupCategory(string Key, string LabelKey, IReadOnlyList<string> Capabilities);

/// <summary>Recette R-15, R-19 (mockup validated by the user on 2026-11-09): every capability the capabilities step offers lives in
/// a category, and the categories come in one order: Construction, Automatisation, Analyse, Livraison, then Supervision. The
/// tiles of a category share one grid and have the same size; the production strategy is the one double tile of Livraison.</summary>
internal static class PipelineSetupCategories
{
    /// <summary>The key of the production strategy tile (an exclusive choice between two strategies, drawn as one double tile).</summary>
    public const string ProductionStrategy = "production-strategy";

    public static IReadOnlyList<PipelineSetupCategory> All { get; } =
    [
        new("build", "PipelineSetupCategoryBuild", ["ci", "packages"]),
        new("automation", "PipelineSetupCategoryAutomation", ["nightly", "dependency-updates"]),
        new("analysis", "PipelineSetupCategoryAnalysis",
        [
            "quality", "security", "qa", "qa-extended", "security-history",
            PipelineSetupExternalOptions.Security, PipelineSetupExternalOptions.SecurityComplete,
            PipelineSetupExternalOptions.QualityMinimal, PipelineSetupExternalOptions.Quality
        ]),
        new("delivery", "PipelineSetupCategoryDelivery",
        [
            "candidate", "deploy-after-candidate", "deploy-approval", "production", "fast-deploy", "rollback",
            ProductionStrategy, "expand-contract"
        ]),
        new("supervision", "PipelineSetupSupervision", ["telemetry", "audience"])
    ];
}
