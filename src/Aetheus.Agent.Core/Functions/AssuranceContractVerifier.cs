// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>What a verification requires beyond the threshold: available evidence
/// (<c>--require-evidence</c>), and additionally every required test Passed
/// (<c>--require-deployment-readiness</c>).</summary>
internal sealed record AssuranceVerificationMode(bool RequireEvidence, bool RequireDeploymentReadiness)
{
    public string Boundary => RequireDeploymentReadiness ? "Deployment readiness" : "Candidate qualification";
}

/// <summary>
/// The checks of <c>deploy/scripts/verify-candidate-assurance-contract.mjs</c>, in its order and with its
/// messages, over the contract parsed by <see cref="JsJson"/>. Every comparison keeps JavaScript's
/// meaning (<c>===</c>, truthiness, <c>String()</c> before a regular expression test,
/// <c>Object.entries</c> of whatever the contract holds), so a contract the script accepts is accepted
/// and one it refuses is refused; where the script would have crashed on a <c>TypeError</c>, the
/// verification fails too.
/// </summary>
internal sealed partial class AssuranceContractVerifier(
    string sourceSha, string minimumGrade, AssuranceVerificationMode mode, string contractRoot)
{
    private const string ExtendedAnalysisKeys = "dynamicSecurity,extendedDynamicSecurity,quality,security,securityHistory";
    private static readonly string[] CompatibilityTests = ["agentCompatibility", "previousIntegration", "previousE2E", "previousSmoke"];
    private static readonly string[] ResumablePipelines = ["ci", "quality", "security"];

    [GeneratedRegex("^[0-9a-f]{40}\\z|^[0-9a-f]{64}\\z")]
    private static partial Regex LowerFullSha();

    [GeneratedRegex("^[0-9a-fA-F]{40}\\z|^[0-9a-fA-F]{64}\\z")]
    private static partial Regex FullSha();

    [GeneratedRegex("^[0-9a-f]{64}\\z")]
    private static partial Regex LowerSha256();

    /// <summary>Verifies the contract and returns its overall grade.</summary>
    public async Task<string> VerifyAsync(object? contract, CancellationToken cancellationToken)
    {
        if (!LowerFullSha().IsMatch(sourceSha))
            throw new AssuranceContractException("Artifact source commit is invalid.");
        var schema = CheckSchema(contract);
        var profile = CheckProfile(contract, schema);
        CheckIdentity(contract);
        CheckSeal((JsObject)contract!);
        CheckResume(contract);
        var grades = await CheckAnalysesAsync(contract, cancellationToken).ConfigureAwait(false);
        CheckAnalysisKeys(contract, profile);
        foreach (var name in DeclaredTests(schema))
            CheckTest(contract, profile, name, grades);
        CheckLightAgent(contract, profile);
        return CheckOverall(contract, grades);
    }

    // Schema 1 stays accepted for retained releases: its unconditional V-1 suites provide the evidence
    // schema 2 records under full qualification.
    private static double CheckSchema(object? contract)
    {
        var schema = JsValue.Get(contract, "schema");
        if (!JsValue.StrictEquals(schema, 1d) && !JsValue.StrictEquals(schema, 2d))
            throw new AssuranceContractException("Unsupported assurance contract schema.");
        return (double)schema!;
    }

    private static string CheckProfile(object? contract, double schema)
    {
        var profile = schema == 1 ? "full" : JsValue.Get(contract, "profile");
        if (profile is not ("light" or "full"))
            throw new AssuranceContractException(
                $"Assurance contract qualification profile is missing or invalid ('{JsValue.ToJsString(JsValue.Get(contract, "profile"))}').");
        return (string)profile;
    }

    private void CheckIdentity(object? contract)
    {
        if (!JsValue.LowerCaseEquals(JsValue.Get(contract, "sourceSha"), sourceSha, "contract.sourceSha?"))
            throw new AssuranceContractException("Assurance contract source SHA does not match the candidate artifact.");
        var scale = JsJson.Stringify(AssuranceGrades.Scale.Cast<object?>().ToList());
        if (JsJson.Stringify(JsValue.Get(contract, "gradingScale")) != scale)
            throw new AssuranceContractException("Unsupported assurance grading scale.");
    }

    private static void CheckSeal(JsObject contract)
    {
        var seal = contract["seal"];
        if (!JsValue.StrictEquals(JsValue.GetOptional(seal, "algorithm"), "sha256")
            || !LowerSha256().IsMatch(JsValue.ToJsString(JsValue.Coalesce(JsValue.Get(seal, "digest"), string.Empty))))
            throw new AssuranceContractException("Assurance contract seal is missing or invalid.");
        var withoutSeal = contract.Clone();
        withoutSeal.Remove("seal");
        var actualSeal = Sha256(Encoding.UTF8.GetBytes(JsJson.Stringify(withoutSeal)!));
        if (!JsValue.StrictEquals(actualSeal, JsValue.Get(seal, "digest")))
            throw new AssuranceContractException("Assurance contract seal does not match its contents.");
    }

    private static void CheckResume(object? contract)
    {
        var resume = JsValue.Get(contract, "checkpointResume");
        if (!JsValue.Truthy(resume) || (JsValue.Get(resume, "sourceRunId") is not null
                                        && !JsValue.IsPositiveSafeInteger(JsValue.Get(resume, "sourceRunId"))))
            throw new AssuranceContractException("Checkpoint resume source identity is invalid.");
        foreach (var (pipeline, runId) in JsValue.Entries(JsValue.Coalesce(JsValue.Get(resume, "reusedRuns"), new JsObject())))
        {
            if (!ResumablePipelines.Contains(pipeline, StringComparer.Ordinal)
                || !JsValue.IsPositiveSafeInteger(runId)
                || JsValue.Get(resume, "sourceRunId") is null)
                throw new AssuranceContractException("Checkpoint resume provenance is invalid.");
        }
    }

    private async Task<List<string>> CheckAnalysesAsync(object? contract, CancellationToken cancellationToken)
    {
        var grades = new List<string>();
        foreach (var (name, analysis) in JsValue.Entries(JsValue.Coalesce(JsValue.Get(contract, "analyses"), new JsObject())))
        {
            var grade = CheckAnalysisGrade(name, analysis);
            await CheckAnalysisEvidenceAsync(name, analysis, cancellationToken).ConfigureAwait(false);
            grades.Add(grade);
        }
        return grades;
    }

    private string CheckAnalysisGrade(string name, object? analysis)
    {
        var (status, expected) = ExpectedAnalysisGrade(name, analysis);
        var effective = JsValue.Get(analysis, "effectiveGrade");
        if (!JsValue.StrictEquals(effective, expected))
            throw new AssuranceContractException($"{name} effective grade is inconsistent.");
        if (mode.RequireEvidence && status is not "Available")
            throw new AssuranceContractException($"{mode.Boundary} requires available {name} evidence.");
        // effective === expected, and expected is "F" or a grade letter: two parsed values are never
        // the same object, so a reported grade that is not a string cannot be equal to the effective one.
        var letter = (string)effective!;
        if (BelowThreshold(letter))
            throw new AssuranceContractException($"{name} grade {letter} is below deployment threshold {minimumGrade}.");
        CheckAnalysisRunIdentity(name, analysis, status);
        return letter;
    }

    /// <summary>The analysis status, Available or Unavailable, and the grade it must carry: the reported
    /// letter when the evidence is available and complete, F otherwise.</summary>
    internal static (string Status, object? Expected) ExpectedAnalysisGrade(string name, object? analysis)
    {
        var status = JsValue.GetOptional(analysis, "status");
        if (status is not ("Available" or "Unavailable"))
            throw new AssuranceContractException($"{name} grade status is invalid.");
        var reported = JsValue.Get(analysis, "reportedGrade");
        var expected = status is "Available"
            && AssuranceAnalysisCapture.IsGradeLetter(reported)
            && JsValue.StrictEquals(JsValue.Get(analysis, "completeness"), "Complete") ? reported : "F";
        return ((string)status, expected);
    }

    /// <summary>The pipeline run an analysis names: a positive safe integer when present, and present
    /// whenever the evidence is available.</summary>
    internal static void CheckAnalysisRunIdentity(string name, object? analysis, string status)
    {
        var runId = JsValue.Get(analysis, "pipelineRunId");
        if (runId is not null && !JsValue.IsPositiveSafeInteger(runId))
            throw new AssuranceContractException($"{name} pipeline run identity is invalid.");
        if (status is "Available" && runId is null)
            throw new AssuranceContractException($"{name} available evidence requires a pipeline run identity.");
    }

    private async Task CheckAnalysisEvidenceAsync(string name, object? analysis, CancellationToken cancellationToken)
    {
        if (!JsValue.LowerCaseEquals(JsValue.Get(analysis, "sourceSha"), sourceSha, "analysis.sourceSha?"))
            throw new AssuranceContractException($"{name} evidence belongs to another commit.");
        var hash = JsValue.Get(analysis, "sha256");
        if (!LowerSha256().IsMatch(JsValue.ToJsString(JsValue.Coalesce(hash, string.Empty))))
            throw new AssuranceContractException($"{name} evidence hash is invalid.");
        if (JsValue.Get(analysis, "evidence") is not string { Length: > 0 } evidence)
            throw new AssuranceContractException($"{name} evidence path is invalid.");

        // path.resolve drops a trailing separator; path.relative is lexical, like GetRelativePath.
        var evidencePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(evidence, contractRoot));
        if (Path.GetRelativePath(contractRoot, evidencePath).StartsWith("..", StringComparison.Ordinal))
            throw new AssuranceContractException($"{name} evidence path escapes the artifact.");
        var actualHash = Sha256(await File.ReadAllBytesAsync(evidencePath, cancellationToken).ConfigureAwait(false));
        if (!JsValue.StrictEquals(actualHash, hash))
            throw new AssuranceContractException($"{name} evidence hash does not match the contract.");
    }

    // Light qualification carries only the three base analyses; full qualification may add both
    // extension grades together so every included grade contributes to the overall verdict.
    private static void CheckAnalysisKeys(object? contract, string profile)
    {
        var keys = string.Join(',', JsValue.Keys(JsValue.Coalesce(JsValue.Get(contract, "analyses"), new JsObject()))
            .Order(StringComparer.Ordinal));
        if (keys != "dynamicSecurity,quality,security" && !(profile == "full" && keys == ExtendedAnalysisKeys))
            throw new AssuranceContractException("Assurance contract does not contain the three required analysis grades.");
    }

    // Schema 2 adds the rollback smoke both profiles pay for.
    private static IEnumerable<string> DeclaredTests(double schema) =>
        new[] { "unit", "analyzers", "currentIntegration", "currentE2E", "agentCompatibility", "previousIntegration", "previousE2E", "performance" }
            .Concat(schema >= 2 ? ["previousSmoke"] : []);

    private void CheckTest(object? contract, string profile, string name, List<string> grades)
    {
        var test = JsValue.GetOptional(JsValue.Get(contract, "tests"), name);
        if (!JsValue.Truthy(test))
            throw new AssuranceContractException($"{name} test assurance is missing.");
        var required = JsValue.Get(test, "required");
        if (name == "previousSmoke" && !JsValue.StrictEquals(required, true))
            throw new AssuranceContractException("previousSmoke must be required in every qualification profile.");
        if (JsValue.StrictEquals(required, true))
            grades.Add(CheckRequiredTest(contract, name, test));
        if (JsValue.StrictEquals(required, false)
            && (!JsValue.StrictEquals(JsValue.Get(test, "status"), "NotRun") || JsValue.Get(test, "effectiveGrade") is not null))
            throw new AssuranceContractException($"{name} optional test assurance has an invalid status.");
        // A light candidate may leave exactly one test unrequired for the nightly, and only that one.
        if (JsValue.StrictEquals(required, false)
            && JsValue.StrictEquals(JsValue.Get(test, "reason"), "nightly-profile")
            && (profile != "light" || name != "agentCompatibility"))
            throw new AssuranceContractException($"{name} claims the nightly profile exemption outside a light qualification.");
    }

    private string CheckRequiredTest(object? contract, string name, object? test)
    {
        if (CompatibilityTests.Contains(name, StringComparer.Ordinal))
            CheckCompatibility(contract, name, test);
        var (status, expected) = ExpectedTestGrade(name, test);
        CheckTestReadiness(name, status, expected);
        return expected;
    }

    /// <summary>A required test's status, Passed, Failed or Unavailable, and the grade it must carry:
    /// A when it passed, F otherwise.</summary>
    internal static (string Status, string Expected) ExpectedTestGrade(string name, object? test)
    {
        var status = JsValue.Get(test, "status");
        if (status is not ("Passed" or "Failed" or "Unavailable"))
            throw new AssuranceContractException($"{name} required test assurance status is invalid.");
        var expected = status is "Passed" ? "A" : "F";
        if (!JsValue.StrictEquals(JsValue.Get(test, "effectiveGrade"), expected))
            throw new AssuranceContractException($"{name} effective grade is inconsistent.");
        return ((string)status, expected);
    }

    internal void CheckTestReadiness(string name, string status, string expected)
    {
        if (mode.RequireEvidence && status is "Unavailable")
            throw new AssuranceContractException($"{mode.Boundary} requires available {name} evidence.");
        if (mode.RequireDeploymentReadiness && status is not "Passed")
            throw new AssuranceContractException($"Deployment readiness requires {name} to be Passed, not {status}.");
        if (BelowThreshold(expected))
            throw new AssuranceContractException($"{name} grade {expected} is below deployment threshold {minimumGrade}.");
    }

    private static void CheckCompatibility(object? contract, string name, object? test)
    {
        var testMode = JsValue.Get(test, "mode");
        if (testMode is not ("NMinusOne" or "Bootstrap"))
            throw new AssuranceContractException($"{name} compatibility mode is missing or invalid.");
        if (testMode is "Bootstrap" && !JsValue.StrictEquals(JsValue.Get(contract, "bootstrapAuthorized"), true))
            throw new AssuranceContractException($"{name} used Bootstrap without explicit candidate authorization.");
        if (testMode is "NMinusOne" && !FullSha().IsMatch(JsValue.ToJsString(JsValue.Coalesce(JsValue.Get(test, "tested"), string.Empty))))
            throw new AssuranceContractException($"{name} N-1 tested revision is missing or invalid.");
    }

    private static void CheckLightAgent(object? contract, string profile)
    {
        if (profile != "light")
            return;
        var agent = JsValue.GetOptional(JsValue.Get(contract, "tests"), "agentCompatibility");
        if (JsValue.StrictEquals(JsValue.GetOptional(agent, "required"), false)
            && !JsValue.StrictEquals(JsValue.Get(agent, "reason"), "nightly-profile")
            && !JsValue.StrictEquals(JsValue.Get(agent, "reason"), "not-applicable-to-project"))
            throw new AssuranceContractException("agentCompatibility is unrequired in a light candidate without a declared reason.");
    }

    private string CheckOverall(object? contract, List<string> grades)
    {
        var overallGrade = grades.Aggregate("A", (worst, grade) => Rank(grade) > Rank(worst) ? grade : worst);
        if (!JsValue.StrictEquals(JsValue.Get(contract, "overallGrade"), overallGrade))
            throw new AssuranceContractException("Assurance contract overall grade is inconsistent.");
        if (BelowThreshold(overallGrade))
            throw new AssuranceContractException($"Candidate grade {overallGrade} is below deployment threshold {minimumGrade}.");
        return overallGrade;
    }

    private bool BelowThreshold(string grade) => Rank(grade) > Rank(minimumGrade);

    private static int Rank(string grade) => Array.IndexOf(AssuranceGrades.Scale, grade);

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
