// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

internal static class AnalysisMapper
{
    public static AnalysisPortfolioRowDto ToDto(AnalysisPortfolioRow row) => new()
    {
        AnalysisReportId = row.AnalysisReportId,
        OrganizationId = row.OrganizationId,
        OrganizationName = row.OrganizationName,
        ProjectId = row.ProjectId,
        ProjectName = row.ProjectName,
        PipelineId = row.PipelineId,
        PipelineName = row.PipelineName,
        PipelineRunId = row.PipelineRunId,
        PipelineRunNumber = row.PipelineRunNumber,
        BranchName = row.BranchName,
        CommitHash = row.CommitHash,
        Category = row.Category,
        ScannerName = row.ScannerName,
        ScannerVersion = row.ScannerVersion,
        Status = row.Status,
        GateStatus = row.GateStatus,
        Grade = row.Grade,
        GradeCompleteness = row.GradeCompleteness,
        FindingCount = row.FindingCount,
        NewFindingCount = row.NewFindingCount,
        BlockerCount = row.BlockerCount,
        WarningCount = row.WarningCount,
        CompletedAt = row.CompletedAt,
        RepositoryId = row.RepositoryId,
        Source = row.PipelineRunId is null && row.ScannerKey == CveWatchFindings.ScannerKey ? AnalysisScanSource.CveWatch : AnalysisScanSource.Pipeline
    };

    public static AnalysisReportDto ToDto(AnalysisReportRow row)
    {
        var report = row.Report;
        return new AnalysisReportDto
        {
            Id = report.Id,
            OrganizationId = report.OrganizationId,
            ProjectId = report.ProjectId,
            PipelineRunId = report.PipelineRunId,
            PipelineArtifactId = report.PipelineArtifactId,
            ScannerKey = report.ScannerKey,
            ScannerName = report.ScannerName,
            ScannerVersion = report.ScannerVersion,
            Category = report.Category,
            Status = report.Status,
            Format = report.Format,
            ReportPath = report.ReportPath,
            ContentHash = report.PayloadHash ?? report.ContentHash,
            ContentSize = report.ContentSize,
            BranchName = report.BranchName,
            EnvironmentName = report.EnvironmentName,
            CommitHash = report.CommitHash,
            StageName = report.StageName,
            StepName = report.StepName,
            RuleSetHash = report.RuleSetHash,
            StartedAt = report.StartedAt,
            CompletedAt = report.CompletedAt,
            IsTruncated = report.IsTruncated,
            ErrorMessage = report.ErrorMessage,
            FindingCount = row.FindingCount,
            NewFindingCount = row.NewFindingCount,
            ComponentCount = row.ComponentCount,
            MetricCount = row.MetricCount,
            GateStatus = row.GateStatus,
            Grade = row.Grade,
            GradeCompleteness = row.GradeCompleteness,
            BlockerCount = row.BlockerCount,
            WarningCount = row.WarningCount
        };
    }

    public static AnalysisFindingDto ToDto(AnalysisFindingRow row)
    {
        var finding = row.Finding;
        return new AnalysisFindingDto
        {
            Id = finding.Id,
            ProjectId = finding.ProjectId,
            Fingerprint = finding.Fingerprint,
            FingerprintVersion = finding.FingerprintVersion,
            RuleId = finding.RuleId,
            Category = finding.Category,
            Severity = finding.Severity,
            Confidence = finding.Confidence,
            Cwe = finding.Cwe,
            Title = finding.Title,
            Message = finding.Message,
            HelpUri = finding.HelpUri,
            Status = finding.Status,
            FirstSeenAt = finding.FirstSeenAt,
            LastSeenAt = finding.LastSeenAt,
            ResolvedAt = finding.ResolvedAt,
            Responsible = row.Responsible,
            LatestOccurrence = row.LatestOccurrence is null ? null : ToDto(row.LatestOccurrence)
        };
    }

    public static AnalysisFindingOccurrenceDto ToDto(AnalysisFindingOccurrence occurrence)
    {
        return new AnalysisFindingOccurrenceDto
        {
            Id = occurrence.Id,
            AnalysisReportId = occurrence.AnalysisReportId,
            PipelineRunId = occurrence.AnalysisReport?.PipelineRunId,
            PipelineRunNumber = occurrence.AnalysisReport?.PipelineRun?.BuildNumber,
            RepositoryId = occurrence.AnalysisReport?.PipelineRun?.Pipeline.SourceRepositoryId,
            ToolName = occurrence.ToolName,
            ScannerKey = occurrence.ScannerKey,
            RuleId = occurrence.RuleId,
            FilePath = occurrence.FilePath,
            StartLine = occurrence.StartLine,
            EndLine = occurrence.EndLine,
            Symbol = occurrence.Symbol,
            Message = occurrence.Message,
            BranchName = occurrence.BranchName,
            CommitHash = occurrence.CommitHash,
            IsNew = occurrence.IsNew,
            CreatedAt = occurrence.CreatedAt
        };
    }

    public static AnalysisPolicyDto ToDto(AnalysisPolicy policy) => ToDto(
        policy,
        policy.Id < 0
            ? AnalysisPolicyScope.System
            : policy.ProjectId.HasValue
                ? AnalysisPolicyScope.Project
                : policy.OrganizationId.HasValue
                    ? AnalysisPolicyScope.Organization
                    : AnalysisPolicyScope.Global,
        false,
        false,
        policy.Enabled,
        null);

    public static AnalysisPolicyDto ToDto(ResolvedAnalysisPolicy policy) => ToDto(
        policy.Policy,
        policy.Scope,
        policy.IsInherited,
        policy.IsOverride,
        policy.IsEffective,
        policy.OverriddenScope);

    private static AnalysisPolicyDto ToDto(
        AnalysisPolicy policy,
        AnalysisPolicyScope scope,
        bool isInherited,
        bool isOverride,
        bool isEffective,
        AnalysisPolicyScope? overriddenScope) => new()
        {
            Id = policy.Id,
            OrganizationId = policy.OrganizationId,
            ProjectId = policy.ProjectId,
            PolicyKey = AnalysisPolicyKey.ForExisting(policy),
            Name = policy.Name,
            Category = policy.Category,
            ScannerKey = policy.ScannerKey,
            RuleId = policy.RuleId,
            SeverityThreshold = policy.SeverityThreshold,
            NewFindingsOnly = policy.NewFindingsOnly,
            MetricKey = policy.MetricKey,
            Operator = policy.Operator,
            Threshold = policy.Threshold,
            BranchPattern = policy.BranchPattern,
            EnvironmentPattern = policy.EnvironmentPattern,
            Behavior = policy.Behavior,
            Priority = policy.Priority,
            Enabled = policy.Enabled,
            Version = policy.Version,
            Scope = scope,
            IsInherited = isInherited,
            IsOverride = isOverride,
            IsEffective = isEffective,
            OverriddenScope = overriddenScope,
            CreatedAt = policy.CreatedAt,
            UpdatedAt = policy.UpdatedAt
        };

    public static AnalysisPolicyExceptionDto ToDto(AnalysisPolicyException exception) => new()
    {
        Id = exception.Id,
        ProjectId = exception.ProjectId,
        AnalysisPolicyId = exception.AnalysisPolicyId,
        AnalysisFindingId = exception.AnalysisFindingId,
        Fingerprint = exception.Fingerprint,
        RuleId = exception.RuleId,
        ScannerKey = exception.ScannerKey,
        Category = exception.Category,
        BranchPattern = exception.BranchPattern,
        EnvironmentPattern = exception.EnvironmentPattern,
        Reason = exception.Reason,
        CreatedByUsername = exception.CreatedByUsername,
        ExpiresAt = exception.ExpiresAt,
        RevokedAt = exception.RevokedAt,
        CreatedAt = exception.CreatedAt
    };

    public static AnalysisFindingDecisionDto ToDto(AnalysisFindingDecision decision) => new()
    {
        Id = decision.Id,
        AnalysisFindingId = decision.AnalysisFindingId,
        Status = decision.Status,
        Reason = decision.Reason,
        CreatedByUsername = decision.CreatedByUsername,
        ExpiresAt = decision.ExpiresAt,
        RevokedAt = decision.RevokedAt,
        CreatedAt = decision.CreatedAt
    };

    public static AnalysisMetricDto ToDto(AnalysisMetric metric) => new()
    {
        Id = metric.Id,
        AnalysisReportId = metric.AnalysisReportId,
        Key = metric.Key,
        Value = metric.Value,
        Unit = metric.Unit,
        Scope = metric.Scope,
        Language = metric.Language,
        FilePath = metric.FilePath,
        Symbol = metric.Symbol,
        ToolName = metric.ToolName,
        BaselineValue = metric.BaselineValue,
        Direction = metric.Direction,
        CreatedAt = metric.CreatedAt
    };

    public static AnalysisComponentDto ToDto(AnalysisComponent component) => new()
    {
        Id = component.Id,
        AnalysisReportId = component.AnalysisReportId,
        Name = component.Name,
        Version = component.Version,
        PackageUrl = component.PackageUrl,
        ComponentType = component.ComponentType,
        Licenses = string.IsNullOrWhiteSpace(component.LicensesJson)
            ? []
            : JsonSerializer.Deserialize<List<string>>(component.LicensesJson) ?? [],
        Hash = component.Hash,
        IsDirect = component.IsDirect
    };
}
