// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>verify-delivery-promotion</c> function, port of
/// <c>deploy/scripts/verify-delivery-promotion.mjs</c> (with <c>schema-fingerprint.mjs</c>), the
/// production deployment gate: the candidate delivery contract must be a schema 2 contract whose sealed
/// identity (<see cref="DeliveryContractIdentity"/>) and release version hold, whose artifacts match
/// their digests byte for byte, and whose proven schema states include the live production schema
/// (<see cref="DeliverySchemaGate"/>). It accepts and refuses what the script accepts and refuses, logs
/// the script's lines and fails with the script's messages; where the script crashed on a
/// <c>TypeError</c>, the step fails with V8's message.
///
/// Parameters are the script's arguments, paths relative to the workspace: <c>production_schema</c> and
/// <c>live_contract</c> take <c>-</c> for none, and an empty <c>live_contract</c> reads as <c>-</c>.
///
/// Differences, deliberate: a file that cannot be read fails with the .NET message instead of Node's
/// <c>ENOENT</c>/<c>EISDIR</c> text, and text that is not JSON with the .NET parser's message instead of
/// V8's <c>SyntaxError</c>; a key outside ASCII in an object whose keys must be sorted is refused
/// (<see cref="JsLocaleCompare"/>) where the script would have sorted it; nesting deeper than the
/// .NET stack allows is refused where V8 may have gone deeper.
/// </summary>
public sealed class VerifyDeliveryPromotionFunction : IPipelineFunction
{
    private const string None = "-";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public string Name => PipelineFunctionCatalog.VerifyDeliveryPromotion.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var workspace = context.Workspace;
        var schemaPath = context.Parameters["production_schema"];
        var livePath = context.Parameters.GetValueOrDefault("live_contract") is { Length: > 0 } given ? given : None;

        var candidate = JsJson.Parse(Utf8.GetString(await File.ReadAllBytesAsync(
            Resolve(workspace, context.Parameters["candidate_contract"]), cancellationToken).ConfigureAwait(false)));
        var contract = CheckIdentity(candidate);
        await CheckArtifactsAsync(contract, Resolve(workspace, context.Parameters["artifact_directory"]), cancellationToken).ConfigureAwait(false);

        // Accept only live schema states proven by this candidate or the current release's sealed
        // rollback evidence so deployment never crosses an untested migration path (ADR-042).
        var gate = new DeliverySchemaGate(
            contract["baseline"],
            schemaPath == None ? None : Resolve(workspace, schemaPath),
            livePath == None ? None : Resolve(workspace, livePath),
            line => context.Log(line));
        await gate.VerifyAsync(cancellationToken).ConfigureAwait(false);

        await context.Log(
            $"Verified immutable candidate {JsValue.ToJsString(contract["candidateVersion"])} ({JsValue.ToJsString(contract["candidateId"])}).")
            .ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>The schema, the sealed identity and the release version of the candidate, in the
    /// script's order. Returns the contract, which a schema of 2 proves to be an object.</summary>
    private static JsObject CheckIdentity(object? candidate)
    {
        var schema = JsValue.Get(candidate, "schema");
        // Schema 1 sealed the identity of the production release instead of the schema states it was
        // proven against: it carries no fingerprints, so it cannot be checked under this gate at all.
        if (JsValue.StrictEquals(schema, 1d))
            throw new JsError(
                "This candidate was sealed under the old identity-based contract (schema 1) and carries no "
                + "schema fingerprints. Requalify it to obtain a schema-sealed contract.");
        if (!JsValue.StrictEquals(schema, 2d) || !JsValue.Truthy(JsValue.Get(candidate, "candidateId")))
            throw new JsError("The candidate delivery contract is invalid.");
        var contract = (JsObject)candidate!;
        if (!DeliveryContractIdentity.HasValidIdentity(contract))
            throw new JsError("The candidate delivery contract identity is invalid.");
        // A valid identity makes candidateId the 64-character digest string.
        var candidateId = (string)contract["candidateId"]!;
        var candidateVersion = contract["candidateVersion"];
        var expectedSourceVersion = $"c-{JsValue.ToJsString(contract["sourceSha"])}";
        if (!JsValue.StrictEquals(candidateVersion, expectedSourceVersion)
            && !JsValue.StrictEquals(candidateVersion, $"{expectedSourceVersion}-{candidateId[..12]}"))
            throw new JsError("The candidate release version does not match its immutable identity.");
        return contract;
    }

    /// <summary><c>for (const [name, expected] of Object.entries(candidate.artifacts ?? {}))</c>: each file,
    /// <c>path.join</c>ed under the artifact directory, must hash to its sealed digest.</summary>
    private static async Task CheckArtifactsAsync(JsObject contract, string artifactDirectory, CancellationToken cancellationToken)
    {
        foreach (var (name, expected) in JsValue.Entries(JsValue.Coalesce(contract["artifacts"], new JsObject())))
        {
            var bytes = await File.ReadAllBytesAsync(Path.GetFullPath(Path.Join(artifactDirectory, name)), cancellationToken).ConfigureAwait(false);
            if (!JsValue.StrictEquals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expected))
                throw new JsError($"Candidate artifact '{name}' differs from the validated digest.");
        }
    }

    private static string Resolve(string workspace, string path) => Path.GetFullPath(Path.Combine(workspace, path));
}
