// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Publishes one SARIF file as an analysis report: the file is stored as an immutable run artifact
/// (the backend refuses a completed report without one), then ingested under the given scanner key,
/// where its findings reach the run's Gate tab. Shared by <c>type: lint</c> and the AI step's
/// <c>findings_sarif</c>, so both go through the same ingestion.
/// </summary>
internal static class SarifAnalysisReportPublisher
{
    internal sealed record Publication(
        int RunId,
        string? StageName,
        string RelativePath,
        string FilePath,
        string ArtifactName,
        string ScannerKey,
        string ScannerName,
        AnalysisCategory Category,
        DateTime StartedAt);

    /// <summary>The ingested report, or null when the backend returned none. Transport and validation
    /// errors propagate: each caller decides what a failed publication costs.</summary>
    public static async Task<AnalysisReportDto?> PublishAsync(
        IServerApiClient apiClient,
        TimeProvider timeProvider,
        Publication publication,
        string sarif,
        CancellationToken ct)
    {
        var artifactId = await AnalysisArtifactUploader.UploadFileAsync(
            apiClient, publication.RunId, publication.ArtifactName, publication.StageName,
            publication.RelativePath, publication.FilePath, ct).ConfigureAwait(false);
        return await apiClient.PublishAnalysisReportAsync(publication.RunId, new PublishAnalysisReportRequest
        {
            ScannerKey = publication.ScannerKey,
            ScannerName = publication.ScannerName,
            ScannerVersion = "1",
            Category = publication.Category,
            Status = AnalysisReportStatus.Passed,
            Format = AnalysisReportFormat.Sarif,
            ReportContent = sarif,
            ReportPath = publication.RelativePath,
            PipelineArtifactId = artifactId,
            StageName = publication.StageName,
            StepName = publication.RelativePath,
            StartedAt = publication.StartedAt,
            CompletedAt = timeProvider.GetUtcNow().UtcDateTime
        }, ct).ConfigureAwait(false);
    }
}
