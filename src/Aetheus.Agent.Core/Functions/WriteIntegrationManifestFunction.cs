// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>write-integration-manifest</c> function, port of
/// <c>deploy/scripts/write-integration-manifest.mjs</c>: writes the integration manifest the delivery
/// contract seals, <c>{"schema":1,"sourceCommit":...,"packages":[...]}</c> on one line, the packages
/// copied from the declaration as they are. The contract takes a digest over these bytes, so they are
/// written exactly as the script's <c>JSON.stringify</c> wrote them (see <see cref="ScriptJson"/>).
///
/// Differences, deliberate: the declaration has no default path (the script defaulted to the Aetheus
/// example consumer) and the commit defaults to <c>BUILD_SOURCEVERSION</c>.
/// </summary>
public sealed partial class WriteIntegrationManifestFunction : IPipelineFunction
{
    [GeneratedRegex("^[0-9a-fA-F]{40}\\z")]
    internal static partial Regex FullRevision();

    public string Name => PipelineFunctionCatalog.WriteIntegrationManifest.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");

        var commit = FunctionShell.Input(context, "commit", "BUILD_SOURCEVERSION") ?? string.Empty;
        var declarationPath = Path.Combine(context.Workspace, context.Parameters["declaration"]);
        var output = Path.Combine(context.Workspace, context.Parameters["output"]);
        if (!File.Exists(declarationPath))
            return PipelineFunctionResult.Failure($"The integration declaration {declarationPath} does not exist.");

        var declaration = await File.ReadAllTextAsync(declarationPath, cancellationToken).ConfigureAwait(false);
        var manifest = Build(commit, declaration);
        if (manifest.Error is { } error)
            return PipelineFunctionResult.Failure(error);

        await File.WriteAllTextAsync(output, ScriptJson.Compact(manifest.Value!) + "\n", cancellationToken).ConfigureAwait(false);
        await context.Log($"Integration manifest written to {output}.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>The manifest object, or why it cannot be built. Separate from the file IO so a test
    /// can assert its exact bytes, which the delivery contract's digest is taken over.</summary>
    internal static (JsonObject? Value, string? Error) Build(string commit, string declarationJson)
    {
        if (!FullRevision().IsMatch(commit))
            return (null, "The source commit must be a full 40-character revision.");
        if (JsonNode.Parse(declarationJson)?["packages"] is not JsonArray packages)
            return (null, "The integration declaration must carry a `packages` array.");
        return (new JsonObject
        {
            ["schema"] = 1,
            ["sourceCommit"] = commit,
            ["packages"] = packages.DeepClone()
        }, null);
    }
}
