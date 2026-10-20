// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>The scanner report envelope is refused before ingestion when its timestamps, status,
/// error message, artifact or DAST execution identity do not agree.</summary>
public sealed class AnalysisReportEnvelopeTests
{
    private static readonly DateTime Start = new(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc);

    private static PublishAnalysisReportRequest Valid() => new()
    {
        ScannerKey = "semgrep",
        Category = AnalysisCategory.Sast,
        Status = AnalysisReportStatus.Passed,
        PipelineArtifactId = 12,
        StartedAt = Start,
        CompletedAt = Start.AddMinutes(2)
    };

    private static PublishAnalysisReportRequest ValidDast() => Valid() with
    {
        Category = AnalysisCategory.Dast,
        DastLeaseToken = new string('a', 64),
        DastTargetUrl = "https://staging.example.test/",
        StageName = "QA",
        StepName = "ZAP baseline"
    };

    public static TheoryData<string, PublishAnalysisReportRequest> AcceptedEnvelopes() => new()
    {
        { "passed", Valid() },
        { "failed", Valid() with { Status = AnalysisReportStatus.Failed } },
        { "error with message", Valid() with { Status = AnalysisReportStatus.Error, ErrorMessage = "crash", PipelineArtifactId = null } },
        { "not applicable", Valid() with { Status = AnalysisReportStatus.NotApplicable, PipelineArtifactId = null } },
        { "dast", ValidDast() },
    };

    [Theory]
    [MemberData(nameof(AcceptedEnvelopes))]
    public void ValidateEnvelope_AcceptsAConsistentReport(string scenario, PublishAnalysisReportRequest request)
    {
        var error = Record.Exception(() => AnalysisReportPublisher.ValidateEnvelope(request));

        Assert.True(error is null, $"{scenario}: {error?.Message}");
    }

    public static TheoryData<PublishAnalysisReportRequest, string> RejectedEnvelopes() => new()
    {
        { Valid() with { StartedAt = default }, "Scanner start and completion timestamps are required." },
        { Valid() with { CompletedAt = default }, "Scanner start and completion timestamps are required." },
        { Valid() with { CompletedAt = Start.AddSeconds(-1) }, "Scanner completion timestamp precedes its start timestamp." },
        { Valid() with { ErrorMessage = "should not be here" }, "A passed scanner report cannot carry an error message." },
        { Valid() with { Status = AnalysisReportStatus.Error, ErrorMessage = " " }, "An unsuccessful scanner report must carry an error message." },
        { Valid() with { Status = AnalysisReportStatus.TimedOut }, "An unsuccessful scanner report must carry an error message." },
        { Valid() with { Status = AnalysisReportStatus.Unavailable }, "An unsuccessful scanner report must carry an error message." },
        { Valid() with { PipelineArtifactId = null }, "A completed scanner report requires an immutable pipeline artifact." },
        { Valid() with { Status = AnalysisReportStatus.Failed, PipelineArtifactId = null }, "A completed scanner report requires an immutable pipeline artifact." },
        { ValidDast() with { DastLeaseToken = null }, DastIdentityRequired },
        { ValidDast() with { DastLeaseToken = "short" }, DastIdentityRequired },
        { ValidDast() with { DastTargetUrl = "" }, DastIdentityRequired },
        { ValidDast() with { StageName = null }, DastIdentityRequired },
        { ValidDast() with { StepName = " " }, DastIdentityRequired },
    };

    private const string DastIdentityRequired =
        "A DAST report requires a scheduler-issued execution lease, exact target URL, stage and step identity.";

    [Theory]
    [MemberData(nameof(RejectedEnvelopes))]
    public void ValidateEnvelope_RefusesAnInconsistentReport(PublishAnalysisReportRequest request, string expected)
    {
        var error = Assert.Throws<BadRequestException>(() => AnalysisReportPublisher.ValidateEnvelope(request));

        Assert.Equal(expected, error.Message);
    }
}
