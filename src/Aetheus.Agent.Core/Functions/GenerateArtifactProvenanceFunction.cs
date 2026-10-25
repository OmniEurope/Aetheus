// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json.Nodes;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>generate-artifact-provenance</c> function, port of
/// <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/generate-artifact-provenance.mjs</c>: records, per category of workspace paths, the
/// number of files, their total size and a SHA-256 over each file's relative path, size and digest
/// (<see cref="ArtifactProvenanceDigest"/>), and for the agent archives the archives the release manifest
/// declares. The output must stay inside the workspace and symbolic links are refused, as in the script.
///
/// A project lays its payload out its own way: it names its categories in a JSON file (the
/// <c>categories</c> parameter, else <c>AETHEUS_PROVENANCE_CATEGORIES</c>), every path relative to and
/// inside the workspace. Without one the built-in map is the Aetheus layout the script carried.
///
/// Differences, deliberate: the source revision defaults to <c>BUILD_SOURCEVERSION</c>, and paths are
/// resolved in the workspace where the script resolved them in its working directory (the pipeline ran
/// it from the workspace).
/// </summary>
public sealed class GenerateArtifactProvenanceFunction : IPipelineFunction
{
    /// <summary>The Aetheus layout, the script's built-in map, in its order.</summary>
    internal static readonly IReadOnlyList<KeyValuePair<string, string[]>> AetheusCategories =
    [
        new("dockerImages", [".pipeline-artifacts/aetheus-back.tar.gz", ".pipeline-artifacts/aetheus-front.tar.gz"]),
        new("agentArchives", [".pipeline-artifacts/agent-release"]),
        new("coverage", ["coverage"]),
        new("qaRuntime",
        [
            "tests/Aetheus.Back.IntegrationTests/bin/Release/net10.0",
            "tests/Aetheus.Back.IntegrationTests/obj/project.assets.json",
            "tests/Aetheus.Back.IntegrationTests/obj/Aetheus.Back.IntegrationTests.csproj.nuget.g.props",
            "tests/Aetheus.Back.IntegrationTests/obj/Aetheus.Back.IntegrationTests.csproj.nuget.g.targets",
            ".pipeline-artifacts/aetheus-browser-smoke.tar.gz"
        ]),
        new("buildIntermediates", []),
        new("optionalObservability",
        [
            "src/Aetheus.Telemetry/bin", "src/Aetheus.Telemetry/obj",
            "tests/Aetheus.Telemetry.Tests/bin", "tests/Aetheus.Telemetry.Tests/obj",
            "src/Aetheus.WebAnalytics/bin", "src/Aetheus.WebAnalytics/obj",
            "tests/Aetheus.WebAnalytics.Tests/bin", "tests/Aetheus.WebAnalytics.Tests/obj"
        ]),
        new("contracts",
        [
            ".pipeline-artifacts/source-commit", ".pipeline-artifacts/qa-rollback-contract",
            ".pipeline-artifacts/agent-protocol-contract", ".pipeline-artifacts/integration-manifest.json",
            ".pipeline-artifacts/delivery-contract.json"
        ])
    ];

    public string Name => PipelineFunctionCatalog.GenerateArtifactProvenance.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");

        var commit = FunctionShell.Input(context, "commit", "BUILD_SOURCEVERSION") ?? string.Empty;
        if (!WriteIntegrationManifestFunction.FullRevision().IsMatch(commit))
            return PipelineFunctionResult.Failure("Usage: generate-artifact-provenance <output.json> <40-char-source-commit>");

        var digest = new ArtifactProvenanceDigest(Path.GetFullPath(context.Workspace));
        var output = Path.GetFullPath(Path.Combine(digest.Root, context.Parameters["output"]));
        if (!digest.IsInside(output))
            return PipelineFunctionResult.Failure("The provenance output must stay inside the workspace.");

        var overridePath = FunctionShell.Input(context, "categories", "AETHEUS_PROVENANCE_CATEGORIES");
        var categories = overridePath is null ? AetheusCategories : await digest.LoadCategoriesAsync(overridePath, cancellationToken).ConfigureAwait(false);

        var manifest = new JsonObject
        {
            ["schema"] = 1,
            ["sourceCommit"] = commit.ToLowerInvariant(),
            ["categories"] = await digest.SummarizeAllAsync(categories, cancellationToken).ConfigureAwait(false)
        };
        await File.WriteAllTextAsync(output, ScriptJson.Compact(manifest) + "\n", cancellationToken).ConfigureAwait(false);
        await context.Log($"Artifact provenance written to {output}.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }
}
