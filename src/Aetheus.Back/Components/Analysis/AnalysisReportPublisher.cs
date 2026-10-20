// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Aetheus.Back.Components.Analysis;

internal sealed class AnalysisReportPublisher(
    IAnalysisRepository repository,
    AnalysisIngestGate ingestGate,
    AnalysisPolicyEngine policyEngine,
    TimeProvider timeProvider,
    IDbTransactionScope transaction,
    IEntityChangeNotifier notifier,
    INotificationService notifications,
    IDependencyTrackOutbox dependencyTrackOutbox,
    IOptions<AnalysisPlatformOptions> platformOptions)
{
    private readonly AnalysisPlatformOptions _platformOptions = platformOptions.Value;

    public async Task<AnalysisReportDto> PublishAsync(
        int runId,
        PublishAnalysisReportRequest request,
        CancellationToken ct)
    {
        request = NormalizeRequest(request);
        ValidateEnvelope(request);
        var queue = Stopwatch.StartNew();
        PublishReportOutcome outcome;
        await using (await ingestGate.EnterAsync(runId, ct).ConfigureAwait(false))
        {
            AnalysisTelemetry.RecordQueue(queue.Elapsed);
            var ingestion = Stopwatch.StartNew();
            outcome = await PublishCoreAsync(runId, request, ct).ConfigureAwait(false);
            if (outcome.Created)
            {
                AnalysisTelemetry.RecordReport(
                    ingestion.Elapsed,
                    Encoding.UTF8.GetByteCount(request.ReportContent),
                    outcome.Report.FindingCount,
                    request.Status,
                    outcome.Report.GateStatus ?? AnalysisGateStatus.Error);
            }
        }

        var report = outcome.Report;
        if (!outcome.Created
            && request.Category == AnalysisCategory.Sbom
            && request.Status is AnalysisReportStatus.Passed or AnalysisReportStatus.Failed)
            await dependencyTrackOutbox.EnqueueSbomAsync(report.Id, ct).ConfigureAwait(false);
        if (!outcome.Created) return report;

        await notifier.BroadcastAsync(
            ResourceType.Project,
            report.ProjectId,
            EntityChangeOps.Updated,
            ct,
            organizationId: report.OrganizationId).ConfigureAwait(false);
        await notifications.SendEventAsync("analysis.report.completed", new
        {
            report.Id,
            report.OrganizationId,
            report.ProjectId,
            report.PipelineRunId,
            report.ScannerKey,
            report.Status,
            report.GateStatus,
            report.FindingCount,
            report.NewFindingCount
        }, ct).ConfigureAwait(false);
        if (report.GateStatus is AnalysisGateStatus.Blocked or AnalysisGateStatus.Error or AnalysisGateStatus.Warning)
        {
            await notifications.SendEventAsync(
                $"analysis.gate.{report.GateStatus.Value.ToString().ToLowerInvariant()}",
                new
                {
                    report.Id,
                    report.OrganizationId,
                    report.ProjectId,
                    report.PipelineRunId,
                    report.GateStatus,
                    report.BlockerCount,
                    report.WarningCount
                },
                ct).ConfigureAwait(false);
        }

        return report;
    }

    private async Task<PublishReportOutcome> PublishCoreAsync(
        int runId,
        PublishAnalysisReportRequest request,
        CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(request.ReportContent);
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var existingReport = await repository.GetReportByIdentityAsync(
            runId, request.ScannerKey, request.StageName, request.StepName, payloadHash, ct).ConfigureAwait(false);
        if (existingReport is not null)
            return new PublishReportOutcome(AnalysisMapper.ToDto(existingReport), false);

        var context = await repository.GetRunContextAsync(runId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("Pipeline run not found or not owned by a project.");
        if (request.Category == AnalysisCategory.Dast)
            await ValidateDastLeaseAsync(request, runId, ct).ConfigureAwait(false);

        var usage = await repository.GetProjectIngestUsageAsync(
            context.ProjectId,
            timeProvider.GetUtcNow().UtcDateTime.AddDays(-1),
            ct).ConfigureAwait(false);
        var incomingBytes = Encoding.UTF8.GetByteCount(request.ReportContent);
        if (usage.Count >= Math.Clamp(_platformOptions.ProjectDailyReportLimit, 1, 100_000)
            || usage.Bytes + incomingBytes >
            Math.Clamp(_platformOptions.ProjectDailyBytesLimit, 1_048_576, 107_374_182_400))
            throw new ConflictException("The project analysis ingestion quota for the rolling 24-hour window is exceeded.");

        if (!string.IsNullOrWhiteSpace(request.EnvironmentName))
        {
            var environmentName = request.EnvironmentName.Trim();
            if (!await repository.EnvironmentBelongsToProjectAsync(
                    environmentName, context.ProjectId, ct).ConfigureAwait(false))
                throw new BadRequestException("The report environment does not belong to this project.");
            context = context with { EnvironmentName = environmentName };
        }

        if (request.PipelineArtifactId.HasValue
            && !await repository.ArtifactBelongsToRunAsync(
                request.PipelineArtifactId.Value, runId, ct).ConfigureAwait(false))
            throw new BadRequestException("The report artifact does not belong to this pipeline run.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var report = CreateReport(context, runId, request, payloadHash, bytes.LongLength);
        AnalysisEvaluation? evaluation = null;
        var storedMetricCount = 0;
        async Task PersistAsync()
        {
            if (request.Status is AnalysisReportStatus.Passed or AnalysisReportStatus.Failed)
            {
                await AnalysisReportNormalizer.AddAsync(
                    repository, report, context, request.ReportContent, now, ct).ConfigureAwait(false);
            }
            await repository.AddReportAsync(report, ct).ConfigureAwait(false);
            // Graded and gated on every metric, then only the ones an analysis keeps are written.
            var flagged = new HashSet<AnalysisMetric>(ReferenceEqualityComparer.Instance);
            evaluation = await policyEngine.EvaluateAsync(report, context, now, flagged, ct).ConfigureAwait(false);
            var stored = AnalysisMetricRetention.Select([.. report.Metrics], flagged);
            await repository.AddMetricsAsync(report.Id, stored, ct).ConfigureAwait(false);
            storedMetricCount = stored.Count;
            await repository.AddEvaluationAsync(evaluation, ct).ConfigureAwait(false);
            if (request.Category == AnalysisCategory.Sbom
                && request.Status is AnalysisReportStatus.Passed or AnalysisReportStatus.Failed)
                await dependencyTrackOutbox.EnqueueSbomAsync(report.Id, ct).ConfigureAwait(false);
        }

        try
        {
            if (transaction.IsRelational)
                await transaction.ExecuteInTransactionAsync(PersistAsync, ct).ConfigureAwait(false);
            else
                await PersistAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            var concurrent = await repository.RecoverReportAfterWriteConflictAsync(
                runId, request.ScannerKey, request.StageName, request.StepName, payloadHash, ct).ConfigureAwait(false);
            if (concurrent is not null)
                return new PublishReportOutcome(AnalysisMapper.ToDto(concurrent), false);
            throw;
        }

        var persistedEvaluation = evaluation
            ?? throw new InvalidOperationException("Analysis report evaluation was not persisted.");
        return new PublishReportOutcome(AnalysisMapper.ToDto(new AnalysisReportRow(
            report,
            report.Occurrences.Select(occurrence => occurrence.AnalysisFinding).Distinct().Count(),
            report.Occurrences.Count(occurrence => occurrence.IsNew),
            report.Components.Count,
            storedMetricCount,
            persistedEvaluation.Status,
            persistedEvaluation.Grade,
            persistedEvaluation.GradeCompleteness,
            persistedEvaluation.BlockerCount,
            persistedEvaluation.WarningCount)), true);
    }

    private async Task ValidateDastLeaseAsync(
        PublishAnalysisReportRequest request,
        int runId,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(request.DastTargetUrl, UriKind.Absolute, out var target)
            || target.Scheme is not ("http" or "https"))
            throw new BadRequestException("A DAST report requires its exact absolute target URL.");
        var targetPort = target.IsDefaultPort
            ? target.Scheme == Uri.UriSchemeHttp ? 80 : 443
            : target.Port;
        if (!await repository.DastLeaseAuthorizesReportAsync(
                request.DastLeaseToken!,
                runId,
                target.Host.TrimEnd('.'),
                targetPort,
                timeProvider.GetUtcNow().UtcDateTime,
                ct).ConfigureAwait(false))
            throw new BadRequestException("The DAST execution lease is missing, expired, or does not authorize this run and target.");
    }

    private static AnalysisReport CreateReport(
        AnalysisRunContext context,
        int runId,
        PublishAnalysisReportRequest request,
        string payloadHash,
        long contentSize) => new()
        {
            OrganizationId = context.OrganizationId,
            ProjectId = context.ProjectId,
            PipelineRunId = runId,
            PipelineArtifactId = request.PipelineArtifactId,
            ScannerKey = request.ScannerKey,
            ScannerName = request.ScannerName,
            ScannerVersion = request.ScannerVersion,
            Category = request.Category,
            Status = request.Status,
            Format = request.Format,
            ReportPath = request.ReportPath,
            ContentHash = BuildLegacyIdentityHash(request, payloadHash),
            PayloadHash = payloadHash,
            ContentSize = contentSize,
            BranchName = context.BranchName,
            EnvironmentName = context.EnvironmentName,
            CommitHash = context.CommitHash,
            StageName = request.StageName,
            StepName = request.StepName,
            RuleSetHash = request.RuleSetHash,
            StartedAt = request.StartedAt,
            CompletedAt = request.CompletedAt,
            IsTruncated = request.IsTruncated,
            ErrorMessage = request.ErrorMessage
        };

    private static string BuildLegacyIdentityHash(PublishAnalysisReportRequest request, string payloadHash)
    {
        if (request.Category != AnalysisCategory.Dast) return payloadHash;
        var identity = Encoding.UTF8.GetBytes($"{request.StageName}\n{request.StepName}\n{payloadHash}");
        return Convert.ToHexStringLower(SHA256.HashData(identity));
    }

    internal static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    internal static void ValidateEnvelope(PublishAnalysisReportRequest request)
    {
        if (request.StartedAt.Year < 2000 || request.CompletedAt.Year < 2000)
            throw new BadRequestException("Scanner start and completion timestamps are required.");
        if (request.CompletedAt < request.StartedAt)
            throw new BadRequestException("Scanner completion timestamp precedes its start timestamp.");
        if (request.Status == AnalysisReportStatus.Passed && !string.IsNullOrWhiteSpace(request.ErrorMessage))
            throw new BadRequestException("A passed scanner report cannot carry an error message.");
        if (request.Status is AnalysisReportStatus.Error or AnalysisReportStatus.TimedOut or AnalysisReportStatus.Unavailable
            && string.IsNullOrWhiteSpace(request.ErrorMessage))
            throw new BadRequestException("An unsuccessful scanner report must carry an error message.");
        if (request.Status is AnalysisReportStatus.Passed or AnalysisReportStatus.Failed
            && !request.PipelineArtifactId.HasValue)
            throw new BadRequestException("A completed scanner report requires an immutable pipeline artifact.");
        if (request.Category == AnalysisCategory.Dast)
            ValidateDastEnvelope(request);
    }

    private static void ValidateDastEnvelope(PublishAnalysisReportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DastLeaseToken)
            || request.DastLeaseToken.Length != 64
            || string.IsNullOrWhiteSpace(request.DastTargetUrl)
            || string.IsNullOrWhiteSpace(request.StageName)
            || string.IsNullOrWhiteSpace(request.StepName))
            throw new BadRequestException(
                "A DAST report requires a scheduler-issued execution lease, exact target URL, stage and step identity.");
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PublishAnalysisReportRequest NormalizeRequest(PublishAnalysisReportRequest request) => request with
    {
        ScannerKey = request.ScannerKey.Trim(),
        ScannerName = request.ScannerName.Trim(),
        ScannerVersion = request.ScannerVersion.Trim(),
        ReportPath = Normalize(request.ReportPath),
        StageName = Normalize(request.StageName),
        StepName = Normalize(request.StepName),
        EnvironmentName = Normalize(request.EnvironmentName),
        RuleSetHash = Normalize(request.RuleSetHash),
        DastLeaseToken = Normalize(request.DastLeaseToken),
        DastTargetUrl = Normalize(request.DastTargetUrl),
        ErrorMessage = Normalize(request.ErrorMessage)
    };

    private sealed record PublishReportOutcome(AnalysisReportDto Report, bool Created);
}
