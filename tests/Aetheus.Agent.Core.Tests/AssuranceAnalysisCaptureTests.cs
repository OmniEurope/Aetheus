// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The seal says the real cause of a summary without a grade (owner decision after the candidate of run #2958 failed
/// with "quality summary belongs to another commit" when the summary simply had no grade): nothing measured, or a
/// grading the validator rejected, is recorded as an unavailable grade F with its reason; only a summary that carries
/// another commit is refused as one.
/// </summary>
public sealed class AssuranceAnalysisCaptureTests : IDisposable
{
    private const string SourceSha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aetheus-capture-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ASummaryWithNoGrade_IsRecordedAsNothingMeasured_NotAnotherCommit()
    {
        var capture = Prepare("""{"pipelineRunId":11,"status":"Passed","grade":null}""");

        var analysis = await capture.CaptureAsync("quality", Input, "quality-summary.json", TestContext.Current.CancellationToken);

        Assert.Equal("Unavailable", analysis["status"]);
        Assert.Equal("no-grade", analysis["reason"]);
        Assert.Equal("quality summary has no grade: nothing measured.", analysis["detail"]);
        Assert.Equal("F", analysis["effectiveGrade"]);
        Assert.Equal(SourceSha, analysis["sourceSha"]);
        Assert.Equal(11d, analysis["pipelineRunId"]);
    }

    [Fact]
    public async Task ASummaryWhoseGradingWasRejected_SaysSoWithTheValidatorMessage()
    {
        var capture = Prepare("""
            {"pipelineRunId":11,"status":"Passed","grade":{"completeness":"Incomplete","domains":[{"domain":"CodeQuality","measures":[
              {"key":"grade.duplication","rejection":"analysis grading rule 'grade.duplication' must define finite A, B, C, D and E thresholds."}]}]}}
            """);

        var analysis = await capture.CaptureAsync("quality", Input, "quality-summary.json", TestContext.Current.CancellationToken);

        Assert.Equal("Unavailable", analysis["status"]);
        Assert.Equal("no-grade", analysis["reason"]);
        Assert.Equal(
            "quality summary has no grade (grading rejected: analysis grading rule 'grade.duplication' must define finite A, B, C, D and E thresholds.).",
            analysis["detail"]);
    }

    [Fact]
    public async Task ASummaryOfAnotherCommit_IsStillRefusedWithTheCommitMessage()
    {
        var capture = Prepare("""
            {"pipelineRunId":11,"status":"Passed","grade":{"overallGrade":"A","completeness":"Complete","commitHash":"ffffffffffffffffffffffffffffffffffffffff","pipelineRunId":11}}
            """);

        var error = await Assert.ThrowsAsync<AssuranceContractException>(() =>
            capture.CaptureAsync("quality", Input, "quality-summary.json", TestContext.Current.CancellationToken));

        Assert.Equal("quality summary belongs to another commit.", error.Message);
    }

    private string Input => Path.Combine(_root, "input");

    private AssuranceAnalysisCapture Prepare(string summary)
    {
        Directory.CreateDirectory(Input);
        var evidence = Path.Combine(_root, "artifact", "assurance");
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(Input, "quality-summary.json"), summary);
        return new AssuranceAnalysisCapture(SourceSha, evidence, Path.Combine(_root, "artifact"));
    }
}
