// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

internal static class AnalysisReportNormalizer
{
    public static async Task AddAsync(
        IAnalysisRepository repository,
        AnalysisReport report,
        AnalysisRunContext context,
        string content,
        DateTime now,
        CancellationToken ct)
    {
        if (report.IsTruncated)
            throw new BadRequestException("A truncated report cannot be published as passed or failed.");
        if (string.IsNullOrWhiteSpace(content))
            throw new BadRequestException("A completed scanner report cannot be empty.");
        if (report.Format == AnalysisReportFormat.NativeJson && report.Category == AnalysisCategory.Dast)
        {
            await AddFindingResultsAsync(repository, report, context, ZapAnalysisParser.Parse(content), now, ct).ConfigureAwait(false);
            return;
        }
        if (report.Format == AnalysisReportFormat.NativeJson
            && string.Equals(report.ScannerKey, "eslint", StringComparison.OrdinalIgnoreCase))
        {
            await AddFindingResultsAsync(repository, report, context, EslintAnalysisParser.Parse(content), now, ct).ConfigureAwait(false);
            return;
        }
        if (report.Format == AnalysisReportFormat.NativeJson
            && string.Equals(report.ScannerKey, "jscpd", StringComparison.OrdinalIgnoreCase))
        {
            var parsedJscpd = JscpdAnalysisParser.Parse(content);
            await AddFindingResultsAsync(repository, report, context, parsedJscpd.Findings, now, ct).ConfigureAwait(false);
            await AddMetricResultsAsync(repository, report, context, parsedJscpd.Metrics, now, ct).ConfigureAwait(false);
            return;
        }
        if (report.Format == AnalysisReportFormat.MetricsJson)
        {
            await AddMetricResultsAsync(repository, report, context, AnalysisMetricParser.Parse(content), now, ct).ConfigureAwait(false);
            return;
        }
        if (report.Format != AnalysisReportFormat.Sarif)
        {
            AddComponentResults(report, content);
            return;
        }

        await AddFindingResultsAsync(
            repository, report, context, SarifAnalysisParser.Parse(content, report.Category), now, ct).ConfigureAwait(false);
    }

    private static async Task AddMetricResultsAsync(
        IAnalysisRepository repository,
        AnalysisReport report,
        AnalysisRunContext context,
        IReadOnlyList<ParsedAnalysisMetric> parsed,
        DateTime now,
        CancellationToken ct)
    {
        var baselineBranch = string.IsNullOrWhiteSpace(context.DefaultBranch) ? "main" : context.DefaultBranch;
        var baseline = await repository.GetBaselineMetricValuesAsync(
            context.ProjectId, baselineBranch, context.PipelineRunId,
            parsed.Select(metric => metric.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct).ConfigureAwait(false);
        foreach (var metric in parsed.DistinctBy(
            item => $"{item.Key}|{item.Scope}|{item.Language}|{item.FilePath}|{item.Symbol}", StringComparer.OrdinalIgnoreCase))
        {
            report.Metrics.Add(new AnalysisMetric
            {
                OrganizationId = context.OrganizationId,
                ProjectId = context.ProjectId,
                Key = metric.Key,
                Value = metric.Value,
                Unit = metric.Unit,
                Scope = metric.Scope,
                Language = metric.Language,
                FilePath = metric.FilePath,
                Symbol = metric.Symbol,
                ToolName = metric.ToolName,
                BaselineValue = baseline.TryGetValue(AnalysisMetricIdentity.Build(
                    metric.Key, metric.Scope, metric.Language, metric.FilePath, metric.Symbol), out var baselineValue)
                    ? baselineValue
                    : null,
                Direction = metric.Direction,
                CreatedAt = now
            });
        }
    }

    private static AnalysisFindingStatus? StandingDecisionStatus(AnalysisFinding finding, DateTime now) =>
        finding.Decisions
            .Where(decision => decision.RevokedAt == null && (decision.ExpiresAt == null || decision.ExpiresAt > now))
            .OrderByDescending(decision => decision.CreatedAt)
            .ThenByDescending(decision => decision.Id)
            .Select(decision => (AnalysisFindingStatus?)decision.Status)
            .FirstOrDefault();

    private static async Task AddFindingResultsAsync(
        IAnalysisRepository repository,
        AnalysisReport report,
        AnalysisRunContext context,
        IReadOnlyList<ParsedAnalysisFinding> parsed,
        DateTime now,
        CancellationToken ct)
    {
        await repository.ReopenExpiredFindingDecisionsAsync(context.ProjectId, now, ct).ConfigureAwait(false);
        var baselineBranch = string.IsNullOrWhiteSpace(context.DefaultBranch) ? "main" : context.DefaultBranch;
        var baseline = await repository.GetBaselineFingerprintsAsync(
            context.ProjectId, baselineBranch, context.PipelineRunId,
            report.ScannerKey, report.Category, ct).ConfigureAwait(false);
        var fingerprints = parsed.Select(finding => finding.Fingerprint).Distinct(StringComparer.Ordinal).ToList();
        var findings = new Dictionary<string, AnalysisFinding>(
            await repository.GetFindingsByFingerprintsAsync(context.ProjectId, fingerprints, ct).ConfigureAwait(false),
            StringComparer.Ordinal);

        foreach (var item in parsed.DistinctBy(finding => finding.LocationHash))
        {
            if (!findings.TryGetValue(item.Fingerprint, out var finding))
            {
                finding = new AnalysisFinding
                {
                    OrganizationId = context.OrganizationId,
                    ProjectId = context.ProjectId,
                    Fingerprint = item.Fingerprint,
                    FingerprintVersion = 2,
                    RuleId = item.RuleId,
                    Category = item.Category,
                    Severity = item.Severity,
                    Confidence = item.Confidence,
                    Cwe = item.Cwe,
                    Title = item.Title,
                    Message = item.Message,
                    HelpUri = item.HelpUri,
                    FirstSeenAt = now,
                    LastSeenAt = now
                };
                findings.Add(item.Fingerprint, finding);
            }
            else
            {
                finding.LastSeenAt = now;
                if (item.Severity > finding.Severity) finding.Severity = item.Severity;
                if (finding.Status == AnalysisFindingStatus.Fixed)
                {
                    // Recette R2-135: a fixed finding drops out of the active ones whatever was decided;
                    // when it comes back, the decision still standing (not revoked, not expired) applies
                    // again, otherwise it is open.
                    finding.Status = StandingDecisionStatus(finding, now) ?? AnalysisFindingStatus.Open;
                    finding.ResolvedAt = null;
                }
            }

            report.Occurrences.Add(new AnalysisFindingOccurrence
            {
                AnalysisFinding = finding,
                LocationHash = item.LocationHash,
                ToolName = item.ToolName,
                ScannerKey = report.ScannerKey,
                RuleId = item.RuleId,
                FilePath = item.FilePath,
                StartLine = item.StartLine,
                EndLine = item.EndLine,
                Symbol = item.Symbol,
                Message = item.Message,
                BranchName = context.BranchName,
                CommitHash = context.CommitHash,
                IsNew = !baseline.Contains(item.Fingerprint)
            });
        }

        await repository.MarkMissingFindingsFixedAsync(
            context.ProjectId, report.ScannerKey, report.Category, context.BranchName,
            fingerprints, now, ct).ConfigureAwait(false);
    }

    private static void AddComponentResults(AnalysisReport report, string content)
    {
        IReadOnlyList<ParsedAnalysisComponent> parsed = report.Format switch
        {
            AnalysisReportFormat.CycloneDxJson or AnalysisReportFormat.CycloneDxXml
                => CycloneDxParser.Parse(content, report.Format),
            AnalysisReportFormat.SpdxJson => SpdxJsonParser.Parse(content),
            _ => throw new BadRequestException($"Report format {report.Format} is not handled by an analysis adapter.")
        };

        foreach (var component in parsed.DistinctBy(
            item => item.PackageUrl ?? $"{item.Name}|{item.Version}", StringComparer.OrdinalIgnoreCase))
        {
            report.Components.Add(new AnalysisComponent
            {
                OrganizationId = report.OrganizationId,
                ProjectId = report.ProjectId,
                Name = Truncate(component.Name, 500),
                Version = Truncate(component.Version, 200),
                PackageUrl = TruncateNullable(component.PackageUrl, 2000),
                ComponentType = TruncateNullable(component.ComponentType, 100),
                LicensesJson = JsonSerializer.Serialize(component.Licenses),
                Hash = TruncateNullable(component.Hash, 256),
                IsDirect = component.IsDirect
            });
        }
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
    private static string? TruncateNullable(string? value, int maxLength) => value is null ? null : Truncate(value, maxLength);
}
