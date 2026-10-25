// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>verify-assurance-contract</c> function, port of
/// <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/verify-candidate-assurance-contract.mjs</c>, which gates production deployment:
/// the contract must belong to the artifact's source commit, match its seal (recomputed over the parsed
/// contract exactly as <c>JSON.stringify</c> writes it, through <see cref="JsJson"/>), reference evidence
/// files inside the artifact whose hashes match, grade consistently and reach the minimum grade. It
/// accepts and refuses what the script accepts and refuses, with the script's messages; see
/// <see cref="AssuranceContractVerifier"/>.
///
/// The optional <c>requirement</c> is the script's fourth argument: <c>evidence</c> for
/// <c>--require-evidence</c>, <c>deployment-readiness</c> for <c>--require-deployment-readiness</c>.
///
/// Differences, deliberate: when the contract cannot be read or is not JSON, the refusal keeps the
/// script's prefix (<c>Assurance contract is missing or invalid: </c>) followed by the .NET cause instead
/// of V8's; a missing source-commit file fails with the .NET message where the script crashed.
/// </summary>
public sealed class VerifyAssuranceContractFunction : IPipelineFunction
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public string Name => PipelineFunctionCatalog.VerifyAssuranceContract.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var requirement = context.Parameters.GetValueOrDefault("requirement") is { Length: > 0 } given ? given : "none";
        if (requirement is not ("none" or "evidence" or "deployment-readiness"))
            return PipelineFunctionResult.Failure($"Unsupported assurance verification mode '{requirement}'.");
        var minimumGrade = context.Parameters["minimum_grade"];
        if (!AssuranceGrades.Scale.Contains(minimumGrade, StringComparer.Ordinal))
            return PipelineFunctionResult.Failure($"Invalid deployment grade threshold '{minimumGrade}'.");

        var contractPath = Path.GetFullPath(Path.Combine(context.Workspace, context.Parameters["contract"]));
        object? contract;
        try
        {
            contract = JsJson.Parse(Utf8.GetString(await File.ReadAllBytesAsync(contractPath, cancellationToken).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InsufficientExecutionStackException)
        {
            return PipelineFunctionResult.Failure($"Assurance contract is missing or invalid: {ex.Message}");
        }

        var sourceCommit = await File.ReadAllBytesAsync(
            Path.Combine(context.Workspace, context.Parameters["source_commit"]), cancellationToken).ConfigureAwait(false);
        var sourceSha = JsValue.AsciiLower(JsValue.Trim(Utf8.GetString(sourceCommit)));
        var mode = new AssuranceVerificationMode(
            RequireEvidence: requirement != "none", RequireDeploymentReadiness: requirement == "deployment-readiness");
        var verifier = new AssuranceContractVerifier(sourceSha, minimumGrade, mode, Path.GetDirectoryName(contractPath)!);
        var overallGrade = await verifier.VerifyAsync(contract, cancellationToken).ConfigureAwait(false);

        await context.Log(mode.RequireDeploymentReadiness
            ? $"Candidate deployment readiness verified with all required evidence Passed or Available at grade {overallGrade}."
            : $"Candidate assurance grade {overallGrade} verified at deployment threshold {minimumGrade}.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }
}
