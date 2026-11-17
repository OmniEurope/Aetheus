// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The grade checks of an analysis and of a required test, split out of the assurance contract
/// verification, on parsed JSON values and with the verification script's messages.
/// </summary>
public sealed class AssuranceContractGradeChecksTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static AssuranceContractVerifier Verifier(string minimumGrade = "C", bool evidence = false, bool readiness = false) =>
        new(Sha, minimumGrade, new AssuranceVerificationMode(evidence, readiness), Path.GetTempPath());

    private static string Refusal(Action check) => Assert.Throws<AssuranceContractException>(check).Message;

    [Theory]
    [InlineData("""{"status":"Available","reportedGrade":"B","completeness":"Complete"}""", "Available", "B")]
    [InlineData("""{"status":"Available","reportedGrade":"B","completeness":"Partial"}""", "Available", "F")]
    [InlineData("""{"status":"Available","reportedGrade":"Z","completeness":"Complete"}""", "Available", "F")]
    [InlineData("""{"status":"Unavailable","reportedGrade":"A","completeness":"Complete"}""", "Unavailable", "F")]
    public void ExpectedAnalysisGrade_IsTheCompleteReportedLetterOrF(string analysis, string status, string expected)
    {
        var (actualStatus, actualExpected) = AssuranceContractVerifier.ExpectedAnalysisGrade("quality", JsJson.Parse(analysis));

        Assert.Equal(status, actualStatus);
        Assert.Equal(expected, actualExpected);
    }

    [Theory]
    [InlineData("""{"status":"Pending"}""")]
    [InlineData("""{}""")]
    public void ExpectedAnalysisGrade_AnUnknownStatus_IsRefused(string analysis) =>
        Assert.Equal("quality grade status is invalid.",
            Refusal(() => AssuranceContractVerifier.ExpectedAnalysisGrade("quality", JsJson.Parse(analysis))));

    [Theory]
    [InlineData("""{"pipelineRunId":12}""", "Available")]
    [InlineData("""{"pipelineRunId":null}""", "Unavailable")]
    [InlineData("""{"pipelineRunId":7}""", "Unavailable")]
    public void CheckAnalysisRunIdentity_AcceptsAPositiveRunOrNoneWhenUnavailable(string analysis, string status) =>
        AssuranceContractVerifier.CheckAnalysisRunIdentity("security", JsJson.Parse(analysis), status);

    [Theory]
    [InlineData("""{"pipelineRunId":0}""", "Unavailable", "security pipeline run identity is invalid.")]
    [InlineData("""{"pipelineRunId":"12"}""", "Available", "security pipeline run identity is invalid.")]
    [InlineData("""{"pipelineRunId":null}""", "Available", "security available evidence requires a pipeline run identity.")]
    public void CheckAnalysisRunIdentity_RefusesAnInvalidOrMissingRun(string analysis, string status, string reason) =>
        Assert.Equal(reason,
            Refusal(() => AssuranceContractVerifier.CheckAnalysisRunIdentity("security", JsJson.Parse(analysis), status)));

    [Theory]
    [InlineData("""{"status":"Passed","effectiveGrade":"A"}""", "Passed", "A")]
    [InlineData("""{"status":"Failed","effectiveGrade":"F"}""", "Failed", "F")]
    [InlineData("""{"status":"Unavailable","effectiveGrade":"F"}""", "Unavailable", "F")]
    public void ExpectedTestGrade_IsAWhenPassedAndFOtherwise(string test, string status, string expected) =>
        Assert.Equal((status, expected), AssuranceContractVerifier.ExpectedTestGrade("unit", JsJson.Parse(test)));

    [Theory]
    [InlineData("""{"status":"NotRun","effectiveGrade":"F"}""", "unit required test assurance status is invalid.")]
    [InlineData("""{"status":"Passed","effectiveGrade":"B"}""", "unit effective grade is inconsistent.")]
    [InlineData("""{"status":"Failed","effectiveGrade":"A"}""", "unit effective grade is inconsistent.")]
    public void ExpectedTestGrade_RefusesAnInvalidStatusOrAnInconsistentGrade(string test, string reason) =>
        Assert.Equal(reason, Refusal(() => AssuranceContractVerifier.ExpectedTestGrade("unit", JsJson.Parse(test))));

    /// <summary>PLAN-006: a passed suite naming flaky tests carries the candidate's flaky grade; a failed one
    /// stays F whatever its flaky tests; a passed one without flaky tests stays A.</summary>
    [Theory]
    [InlineData("""{"status":"Passed","effectiveGrade":"B","flakyTests":"Login"}""", "B", "Passed", "B")]
    [InlineData("""{"status":"Passed","effectiveGrade":"C","flakyTests":"Login"}""", "C", "Passed", "C")]
    [InlineData("""{"status":"Passed","effectiveGrade":"A"}""", "C", "Passed", "A")]
    [InlineData("""{"status":"Failed","effectiveGrade":"F","flakyTests":"Login"}""", "B", "Failed", "F")]
    public void ExpectedTestGrade_GradesAPassedSuiteOnTheCandidateFlakyTests(string test, string flakyGrade, string status, string expected) =>
        Assert.Equal((status, expected), AssuranceContractVerifier.ExpectedTestGrade("unit", JsJson.Parse(test), flakyGrade));

    /// <summary>A grade that does not match the flaky count is refused: flaky tests graded A, a failure
    /// passed off as flaky, or a B with its flaky tests stripped.</summary>
    [Theory]
    [InlineData("""{"status":"Passed","effectiveGrade":"A","flakyTests":"Login"}""", "B")]
    [InlineData("""{"status":"Failed","effectiveGrade":"B","flakyTests":"Login"}""", "B")]
    [InlineData("""{"status":"Passed","effectiveGrade":"B"}""", "B")]
    public void ExpectedTestGrade_RefusesAGradeTheFlakyTestsDoNotGive(string test, string flakyGrade) =>
        Assert.Equal("unit effective grade is inconsistent.",
            Refusal(() => AssuranceContractVerifier.ExpectedTestGrade("unit", JsJson.Parse(test), flakyGrade)));

    [Fact]
    public void CheckTestReadiness_APassedTestAboveTheThreshold_IsAccepted() =>
        Verifier(evidence: true, readiness: true).CheckTestReadiness("unit", "Passed", "A");

    [Fact]
    public void CheckTestReadiness_RefusesInTheScriptOrder()
    {
        Assert.Equal("Candidate qualification requires available unit evidence.",
            Refusal(() => Verifier(evidence: true).CheckTestReadiness("unit", "Unavailable", "F")));
        Assert.Equal("Deployment readiness requires unit to be Passed, not Failed.",
            Refusal(() => Verifier(evidence: true, readiness: true).CheckTestReadiness("unit", "Failed", "F")));
        Assert.Equal("unit grade F is below deployment threshold C.",
            Refusal(() => Verifier().CheckTestReadiness("unit", "Unavailable", "F")));
    }

    [Fact]
    public void CheckTestReadiness_AFailedTestUnderTheLowestThreshold_IsAccepted() =>
        Verifier(minimumGrade: "F").CheckTestReadiness("unit", "Failed", "F");
}
