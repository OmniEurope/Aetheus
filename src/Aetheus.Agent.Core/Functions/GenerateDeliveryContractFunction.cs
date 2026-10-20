// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>generate-delivery-contract</c> function, port of
/// <c>deploy/scripts/generate-delivery-contract.mjs</c>: seals what a candidate is. The identity holds
/// the source revision, the adapter, the schema state before (the production release's, unless this is
/// a bootstrap) and after (the migrations of this very source tree), the enabled integration packages
/// with the digest of the manifest declaring them, and the SHA-256 of every artifact. The candidate id
/// is the SHA-256 of that identity as <c>JSON.stringify</c> writes it with every object's keys sorted by
/// <c>localeCompare</c>; <c>verify-delivery-promotion.mjs</c> recomputes it the same way, so the bytes
/// are reproduced exactly (<see cref="ScriptJson"/>, <see cref="ScriptCollation"/>).
///
/// Differences, deliberate: the positional arguments and the environment inputs are parameters, the
/// latter defaulting to the variables the script read; <c>bootstrap</c> falls back to <c>true</c> as the
/// pipelines that called the script set it; the source revision defaults to <c>BUILD_SOURCEVERSION</c>.
/// </summary>
public sealed partial class GenerateDeliveryContractFunction : IPipelineFunction
{
    [GeneratedRegex("^[0-9a-fA-F]{64}\\z")]
    private static partial Regex Sha256Hex();

    public string Name => PipelineFunctionCatalog.GenerateDeliveryContract.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");

        var inputs = DeliveryContractInputs.Read(context);
        if (inputs.Error is { } usage)
            return PipelineFunctionResult.Failure(usage);

        var contract = await SealAsync(inputs, cancellationToken).ConfigureAwait(false);
        if (contract.Error is { } error)
            return PipelineFunctionResult.Failure(error);

        var sealedContract = contract.Value!;
        await File.WriteAllTextAsync(inputs.Output, ScriptJson.Indented(sealedContract) + "\n", cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(inputs.Output, (UnixFileMode)0b110_100_100);
        context.SetOutput("CANDIDATE_ID", (string)sealedContract["candidateId"]!);
        context.SetOutput("CANDIDATE_VERSION", (string)sealedContract["candidateVersion"]!);
        await context.Log($"Delivery contract {sealedContract["candidateVersion"]} written to {inputs.Output}.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>The sealed contract (identity, candidate id and version, keys sorted), or why it cannot be
    /// sealed, with the script's messages.</summary>
    internal static async Task<(JsonObject? Value, string? Error)> SealAsync(DeliveryContractInputs inputs, CancellationToken cancellationToken)
    {
        var manifestBytes = await File.ReadAllBytesAsync(inputs.Manifest, cancellationToken).ConfigureAwait(false);
        var packages = EnabledPackages(JsonNode.Parse(manifestBytes));
        if (packages.Error is { } packageError)
            return (null, packageError);

        var baseline = Baseline(inputs);
        if (baseline.Error is { } baselineError)
            return (null, baselineError);

        var identity = Canonicalize(new JsonObject
        {
            ["schema"] = 2,
            ["sourceSha"] = inputs.SourceSha.ToLowerInvariant(),
            ["adapter"] = new JsonObject { ["name"] = inputs.AdapterName, ["version"] = inputs.AdapterVersion },
            ["baseline"] = baseline.Value,
            ["injection"] = new JsonObject { ["manifestSha256"] = Sha256(manifestBytes), ["packages"] = packages.Value },
            ["artifacts"] = await ArtifactDigestsAsync(inputs.Artifacts, cancellationToken).ConfigureAwait(false)
        })!.AsObject();

        var candidateId = Sha256(Encoding.UTF8.GetBytes(ScriptJson.Compact(identity)));
        var candidateVersion = inputs.VersionMode == "source" ? inputs.CandidatePrefix : $"{inputs.CandidatePrefix}-{candidateId[..12]}";
        identity["candidateId"] = candidateId;
        identity["candidateVersion"] = candidateVersion;
        return (Canonicalize(identity)!.AsObject(), null);
    }

    /// <summary>The enabled packages (<c>enabled === true</c>) as <c>{id, version, sha256}</c>, sorted by
    /// id. Each needs a string id and version and a 64-character SHA-256.</summary>
    private static (JsonArray? Value, string? Error) EnabledPackages(JsonNode? manifest)
    {
        var packages = new List<JsonObject>();
        if (manifest?["packages"] is not ({ } declared))
            return (new JsonArray(), null);
        if (declared is not JsonArray items)
            return (null, "The integration manifest's packages is not an array.");
        foreach (var item in items)
        {
            // The script read item.enabled: a null entry threw, any other non-object was skipped.
            if (item is null)
                return (null, "The integration manifest holds a null package entry.");
            if (item is not JsonObject || item["enabled"] is not JsonValue enabled || !enabled.TryGetValue<bool>(out var on) || !on)
                continue;
            if (Text(item["id"]) is not { } id || Text(item["version"]) is not { } version
                || Text(item["sha256"]) is not { } sha || !Sha256Hex().IsMatch(sha))
                return (null, "Every enabled integration requires id, exact version and sha256.");
            packages.Add(new JsonObject { ["id"] = id, ["version"] = version, ["sha256"] = sha.ToLowerInvariant() });
        }
        packages.Sort((left, right) => ScriptCollation.Instance.Compare((string)left["id"]!, (string)right["id"]!));
        return (new JsonArray([.. packages]), null);
    }

    /// <summary>The schema states: the production release's (refused unless it matches its declared
    /// fingerprint) and the one this source tree migrates to.</summary>
    private static (JsonObject? Value, string? Error) Baseline(DeliveryContractInputs inputs)
    {
        // "-" is for a delivery with no database. Any other value must yield migrations: an empty
        // directory means they never reached the packaging step, and sealing an empty state would let
        // the gate match anything.
        var after = inputs.MigrationsDirectory == "-" ? [] : SchemaFingerprint.FromSource(inputs.MigrationsDirectory);
        var before = inputs.Bootstrap ? null : inputs.BaselineMigrations();
        if (!inputs.Bootstrap && !Sha256Hex().IsMatch(inputs.BaselineSchemaHash))
            return (null, "A non-bootstrap candidate requires the schema state of the production release.");
        if (before is not null && SchemaFingerprint.Hash(before) != inputs.BaselineSchemaHash.ToLowerInvariant())
            return (null, "The production schema list does not match its declared fingerprint.");
        if (inputs.MigrationsDirectory != "-" && after.Count == 0)
            return (null, $"No migrations were found in '{inputs.MigrationsDirectory}'; the schema state cannot be sealed. Pass '-' only for a delivery that has no database.");

        return before is null
            ? (new JsonObject { ["bootstrap"] = true, ["schemaAfter"] = SchemaFingerprint.Of(after) }, null)
            : (new JsonObject
            {
                ["bootstrap"] = false,
                ["schemaBefore"] = SchemaFingerprint.Of(before),
                ["schemaAfter"] = SchemaFingerprint.Of(after)
            }, null);
    }

    /// <summary>File name to SHA-256 of each artifact; a later artifact of the same name wins, as the
    /// script's object assignment did.</summary>
    private static async Task<JsonObject> ArtifactDigestsAsync(IReadOnlyList<string> artifacts, CancellationToken cancellationToken)
    {
        var digests = new JsonObject();
        foreach (var artifact in artifacts)
        {
            await using var stream = File.OpenRead(artifact);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            digests[Path.GetFileName(artifact)] = Convert.ToHexStringLower(hash);
        }
        return digests;
    }

    /// <summary>The script's <c>canonicalize</c>: every object rebuilt with its keys in
    /// <c>localeCompare</c> order, arrays kept in their order.</summary>
    internal static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject entries => new JsonObject(entries
            .OrderBy(entry => entry.Key, ScriptCollation.Instance)
            .Select(entry => KeyValuePair.Create(entry.Key, Canonicalize(entry.Value)))
            .ToList()),
        JsonArray items => new JsonArray([.. items.Select(Canonicalize)]),
        null => null,
        _ => node.DeepClone()
    };

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
