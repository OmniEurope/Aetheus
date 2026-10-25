// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.Json;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>verify-schema-contract</c> function, port of the former <c>verify-schema-contract.mjs</c> script
/// and of the fingerprint of <c>schema-fingerprint.mjs</c>: the ordered list of applied EF migration
/// ids (trimmed, empty lines dropped, sorted by UTF-16 code unit as JavaScript sorts) hashed with
/// SHA-256 over its <c>\n</c>-joined text. The contract derives <c>schemaAfter</c> from the source tree
/// and inherits <c>schemaBefore</c> from the deployed contract; QA reads both states from a live
/// database, and a mismatch means the contract describes a schema path the code does not produce.
///
/// The step that called the script checked the contract's presence itself (same message) and passed
/// <c>-</c> for a missing or empty V-1 reading; both are this function's now.
/// </summary>
public sealed class VerifySchemaContractFunction : IPipelineFunction
{
    internal const string DefaultContract = ".pipeline-artifacts/delivery-contract.json";

    public string Name => PipelineFunctionCatalog.VerifySchemaContract.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");

        var contractPath = Path.GetFullPath(Parameter(context, "contract") ?? DefaultContract, context.Workspace);
        if (!HasContent(contractPath))
            return PipelineFunctionResult.Failure("No delivery contract in this workspace; schema states cannot be confronted.");
        var afterPath = Path.GetFullPath(context.Parameters["after"], context.Workspace);
        var beforePath = Parameter(context, "before") is { } before ? Path.GetFullPath(before, context.Workspace) : null;

        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(contractPath, cancellationToken).ConfigureAwait(false));
        var baseline = Property(contract.RootElement, "baseline");
        var observedAfter = Fingerprint(await File.ReadAllTextAsync(afterPath, cancellationToken).ConfigureAwait(false));
        var declaredAfter = Property(baseline, "schemaAfter");
        if (!SameHash(observedAfter, declaredAfter))
            return await RefuseAsync(context,
                "The schema produced by migrating to V is not the one the contract declares.",
                $"  observed : {observedAfter}",
                $"  declared : {Describe(declaredAfter)}").ConfigureAwait(false);

        if (Property(baseline, "bootstrap") is { ValueKind: JsonValueKind.True })
        {
            await context.Log($"Bootstrap: schemaAfter proven against a live database {observedAfter}.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }
        if (beforePath is null || !HasContent(beforePath))
            return PipelineFunctionResult.Failure("A non-bootstrap candidate must prove the schema it migrates from.");
        var observedBefore = Fingerprint(await File.ReadAllTextAsync(beforePath, cancellationToken).ConfigureAwait(false));
        var declaredBefore = Property(baseline, "schemaBefore");
        if (!SameHash(observedBefore, declaredBefore))
            return await RefuseAsync(context,
                "The schema V-1 actually runs on is not the one the contract declares as its starting state.",
                $"  observed : {observedBefore}",
                $"  declared : {Describe(declaredBefore)}",
                "Production has moved since this candidate started; requalify it.").ConfigureAwait(false);

        await context.Log($"Schema path proven on a live database: {observedBefore} -> {observedAfter}.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>A migration history as <c>fingerprint(migrationsFromHistory(text))</c> reads it.</summary>
    internal static SchemaState Fingerprint(string history)
    {
        var migrations = history.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Order(StringComparer.Ordinal)
            .ToList();
        // The same hash generate-delivery-contract seals, so the two cannot drift apart.
        return new SchemaState(SchemaFingerprint.Hash(migrations), migrations.Count);
    }

    internal sealed record SchemaState(string Hash, int Count)
    {
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Hash} ({Count} migrations)");
    }

    private static bool SameHash(SchemaState observed, JsonElement declared) =>
        Property(declared, "hash") is { ValueKind: JsonValueKind.String } hash
        && string.Equals(hash.GetString(), observed.Hash, StringComparison.Ordinal);

    /// <summary>The script's <c>describe(state ?? { hash: "none", migrations: [] })</c>.</summary>
    private static string Describe(JsonElement declared)
    {
        if (declared.ValueKind != JsonValueKind.Object)
            return "none (0 migrations)";
        var hash = Property(declared, "hash") is { ValueKind: JsonValueKind.String } value ? value.GetString() : "undefined";
        var count = Property(declared, "migrations") is { ValueKind: JsonValueKind.Array } migrations ? migrations.GetArrayLength() : 0;
        return string.Create(CultureInfo.InvariantCulture, $"{hash} ({count} migrations)");
    }

    private static async Task<PipelineFunctionResult> RefuseAsync(PipelineFunctionContext context, string reason, params string[] details)
    {
        foreach (var line in details)
            await context.Log(line, TaskLogLevel.Error).ConfigureAwait(false);
        return PipelineFunctionResult.Failure(reason);
    }

    /// <summary>A property of an object, or an undefined element (<c>?.</c>).</summary>
    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? Parameter(PipelineFunctionContext context, string name) =>
        context.Parameters.GetValueOrDefault(name) is { Length: > 0 } value ? value : null;

    private static bool HasContent(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
}
