// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>generate-assurance-contract</c> function, port of
/// <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/generate-candidate-assurance-contract.mjs</c>: seals the analysis grades and the
/// test gate outcomes of a qualification into the candidate assurance contract (schema 2), copies or
/// generates one evidence file per analysis, and publishes the overall grade as
/// <c>CANDIDATE_ASSURANCE_GRADE</c>. The contract, the evidence files and the seal
/// (<c>sha256(JSON.stringify(contract))</c>) are the script's byte for byte, through
/// <see cref="JsJson"/>, so <c>verify-assurance-contract</c> and a retained release sealed by the script
/// verify alike; the refusals carry the script's messages.
///
/// The script's arguments are parameters (paths relative to the workspace). The four settings it read
/// from <c>AETHEUS_ASSURANCE_*</c> variables may be given as parameters, which win when not empty; the
/// gate statuses, the compatibility attestations and the checkpoint identities are still read from the
/// variables of the same names.
///
/// Differences, deliberate: giving only one of <c>security_history</c> and <c>extended_qa</c> is refused
/// with its own message (the script printed its usage); an empty parameter counts as absent (the script
/// would have used an empty argument as a path); where the script crashed (a <c>TypeError</c> on a
/// summary that is JSON null, a provenance file missing or not JSON) the step fails with a message of
/// its own.
/// </summary>
public sealed partial class GenerateAssuranceContractFunction : IPipelineFunction
{
    private const string ProfileVariable = "AETHEUS_ASSURANCE_PROFILE";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // A parameter that stands for one of the script's variables.
    private static readonly Dictionary<string, string> SettingParameters = new(StringComparer.Ordinal)
    {
        [ProfileVariable] = "profile",
        ["AETHEUS_ASSURANCE_PERFORMANCE_ENABLED"] = "performance_enabled",
        ["AETHEUS_ASSURANCE_ALLOW_BOOTSTRAP"] = "allow_bootstrap",
        ["AETHEUS_ASSURANCE_INAPPLICABLE_TESTS"] = "inapplicable_tests"
    };

    [GeneratedRegex("^[0-9a-fA-F]{40}\\z|^[0-9a-fA-F]{64}\\z")]
    private static partial Regex FullSha();

    public string Name => PipelineFunctionCatalog.GenerateAssuranceContract.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var history = Optional(context, "security_history");
        var extended = Optional(context, "extended_qa");
        if ((history is null) != (extended is null))
            return PipelineFunctionResult.Failure("security_history and extended_qa must be given together.");
        var sourceSha = context.Parameters["source_sha"];
        if (!FullSha().IsMatch(sourceSha))
            return PipelineFunctionResult.Failure("The candidate source SHA must be a full hexadecimal commit identifier.");

        var evidenceDirectory = WorkspacePath(context, context.Parameters["evidence_directory"]);
        Directory.CreateDirectory(evidenceDirectory);
        var inapplicable = InapplicableTests(Variable(context, "AETHEUS_ASSURANCE_INAPPLICABLE_TESTS"));
        // ?? "full": an unset profile defaults, an empty one is refused like any other value.
        var profile = ProfileSetting(context) ?? "full";
        if (profile is not ("light" or "full"))
            return PipelineFunctionResult.Failure($"AETHEUS_ASSURANCE_PROFILE must be 'light' or 'full', not '{profile}'.");

        var output = WorkspacePath(context, context.Parameters["output"]);
        var capture = new AssuranceAnalysisCapture(sourceSha, evidenceDirectory, Path.GetDirectoryName(output)!);
        var tests = new AssuranceTestOutcomes(name => Variable(context, name), profile, inapplicable).Build();
        var analyses = await CaptureAnalysesAsync(context, capture, profile, history, extended, cancellationToken).ConfigureAwait(false);
        var contract = await AssembleAsync(context, sourceSha, profile, analyses, tests, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllBytesAsync(output, Utf8.GetBytes(JsJson.StringifyIndented(contract) + "\n"), cancellationToken).ConfigureAwait(false);
        var overallGrade = (string)contract["overallGrade"]!;
        await context.Log($"Candidate assurance contract written to {context.Parameters["output"]} with grade {overallGrade}.").ConfigureAwait(false);
        context.SetOutput("CANDIDATE_ASSURANCE_GRADE", overallGrade);
        return PipelineFunctionResult.Success();
    }

    private static async Task<JsObject> CaptureAnalysesAsync(
        PipelineFunctionContext context, AssuranceAnalysisCapture capture, string profile,
        string? history, string? extended, CancellationToken cancellationToken)
    {
        var analyses = new JsObject();
        analyses.Set("quality", await capture.CaptureAsync(
            "quality", WorkspacePath(context, context.Parameters["quality"]), "quality-summary.json", cancellationToken).ConfigureAwait(false));
        analyses.Set("security", await capture.CaptureAsync(
            "security", WorkspacePath(context, context.Parameters["security"]), "security-summary.json", cancellationToken).ConfigureAwait(false));
        analyses.Set("dynamicSecurity", await capture.CaptureAsync(
            "dynamic-security", WorkspacePath(context, context.Parameters["qa"]), "security-summary.json", cancellationToken).ConfigureAwait(false));
        if (history is null)
            return analyses;
        // A light contract carrying them would claim depth its profile excuses; the verifier refuses the
        // same combination.
        if (profile != "full")
            throw new AssuranceContractException("The nightly extension analyses belong to a full qualification only.");
        analyses.Set("securityHistory", await capture.CaptureAsync(
            "security-history", WorkspacePath(context, history), "security-summary.json", cancellationToken).ConfigureAwait(false));
        analyses.Set("extendedDynamicSecurity", await capture.CaptureAsync(
            "extended-dynamic-security", WorkspacePath(context, extended!), "security-summary.json", cancellationToken).ConfigureAwait(false));
        return analyses;
    }

    private static async Task<JsObject> AssembleAsync(
        PipelineFunctionContext context, string sourceSha, string profile, JsObject analyses, JsObject tests,
        CancellationToken cancellationToken)
    {
        var provenanceBytes = await File.ReadAllBytesAsync(
            WorkspacePath(context, context.Parameters["provenance"]), cancellationToken).ConfigureAwait(false);
        var provenance = JsJson.Parse(Utf8.GetString(provenanceBytes));
        var lowerSha = JsValue.AsciiLower(sourceSha);
        var sourceCommit = JsValue.GetOptional(provenance, "sourceCommit");
        if (!JsValue.LowerCaseEquals(sourceCommit, lowerSha, "artifactProvenance?.sourceCommit?"))
            throw new AssuranceContractException("Artifact provenance belongs to another commit.");

        var contract = new JsObject();
        contract.Set("schema", 2d);
        contract.Set("sourceSha", lowerSha);
        contract.Set("profile", profile);
        contract.Set("gradingScale", AssuranceGrades.Scale.Cast<object?>().ToList());
        contract.Set("overallGrade", OverallGrade(analyses, tests));
        contract.Set("bootstrapAuthorized", Variable(context, "AETHEUS_ASSURANCE_ALLOW_BOOTSTRAP") == "true");
        contract.Set("checkpointResume", CheckpointResume(context));
        contract.Set("analyses", analyses);
        contract.Set("tests", tests);
        var references = new JsObject();
        references.Set("sourceSha", sourceCommit);
        references.Set("categories", JsValue.Get(provenance, "categories"));
        references.Set("provenanceSha256", Sha256(provenanceBytes));
        contract.Set("artifactReferences", references);
        var seal = new JsObject();
        seal.Set("algorithm", "sha256");
        seal.Set("digest", Sha256(Utf8.GetBytes(JsJson.Stringify(contract)!)));
        contract.Set("seal", seal);
        return contract;
    }

    /// <summary>The worst grade of every analysis and every required test, as the script reduced it:
    /// a grade outside the scale never counts as worse.</summary>
    private static string OverallGrade(JsObject analyses, JsObject tests)
    {
        var grades = analyses.Properties.Select(analysis => JsValue.GetOptional(analysis.Value, "effectiveGrade"))
            .Concat(tests.Properties
                .Where(test => JsValue.Truthy(JsValue.GetOptional(test.Value, "required")))
                .Select(test => JsValue.GetOptional(test.Value, "effectiveGrade")));
        object? worst = "A";
        foreach (var grade in grades)
            if (AssuranceGrades.IndexOf(grade) > AssuranceGrades.IndexOf(worst))
                worst = grade;
        return (string)worst!;
    }

    private static JsObject CheckpointResume(PipelineFunctionContext context)
    {
        var sourceRunId = Variable(context, "AETHEUS_RESUME_SOURCE_RUN_ID");
        var resume = new JsObject();
        resume.Set("sourceRunId", AssuranceTestOutcomes.IsPositiveInteger(sourceRunId) ? AssuranceTestOutcomes.ToNumber(sourceRunId!) : null);
        var reusedRuns = new JsObject();
        foreach (var (pipeline, variable) in new[]
                 {
                     ("ci", "AETHEUS_CHECKPOINT_CI_RUN_ID"),
                     ("quality", "AETHEUS_CHECKPOINT_QUALITY_RUN_ID"),
                     ("security", "AETHEUS_CHECKPOINT_SECURITY_RUN_ID")
                 })
        {
            var runId = Variable(context, variable);
            if (AssuranceTestOutcomes.IsPositiveInteger(runId))
                reusedRuns.Set(pipeline, AssuranceTestOutcomes.ToNumber(runId!));
        }
        resume.Set("reusedRuns", reusedRuns);
        return resume;
    }

    /// <summary>The names listed in the comma-separated setting, trimmed as JavaScript trims.</summary>
    private static HashSet<string> InapplicableTests(string? setting) =>
        (setting ?? string.Empty).Split(',').Select(JsValue.Trim).Where(entry => entry.Length > 0).ToHashSet(StringComparer.Ordinal);

    /// <summary>A variable the script read, or the parameter that stands for it when that is set. An
    /// empty value reads as unset, which matches every use of these variables but the profile's.</summary>
    private static string? Variable(PipelineFunctionContext context, string name) =>
        SettingParameters.TryGetValue(name, out var parameter)
            ? FunctionShell.Input(context, parameter, name)
            : FunctionShell.Variable(context, name);

    /// <summary>The profile: the parameter when set, else the variable as the script saw it, where an
    /// empty value is not unset (<c>process.env.AETHEUS_ASSURANCE_PROFILE ?? "full"</c>).</summary>
    private static string? ProfileSetting(PipelineFunctionContext context) =>
        Optional(context, "profile")
        ?? (context.RunVariables.TryGetValue(ProfileVariable, out var value) ? value : Environment.GetEnvironmentVariable(ProfileVariable));

    private static string? Optional(PipelineFunctionContext context, string parameter) =>
        context.Parameters.GetValueOrDefault(parameter) is { Length: > 0 } value ? value : null;

    private static string WorkspacePath(PipelineFunctionContext context, string relative) =>
        Path.GetFullPath(Path.Combine(context.Workspace!, relative));

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>The grading scale of the assurance contract, best first.</summary>
internal static class AssuranceGrades
{
    public static readonly string[] Scale = ["A", "B", "C", "D", "E", "F"];

    /// <summary><c>gradingScale.indexOf(grade)</c>: strict equality, so a value that is not one of the
    /// letters is -1.</summary>
    public static int IndexOf(object? grade) => grade is string letter ? Array.IndexOf(Scale, letter) : -1;
}
