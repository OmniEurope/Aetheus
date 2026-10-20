// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class AnalysisReportConfiguration : IEntityTypeConfiguration<AnalysisReport>
{
    public void Configure(EntityTypeBuilder<AnalysisReport> builder)
    {
        builder.Property(e => e.ScannerKey).HasMaxLength(100).IsRequired();
        builder.Property(e => e.ScannerName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.ScannerVersion).HasMaxLength(100).IsRequired();
        builder.Property(e => e.ReportPath).HasMaxLength(1000);
        builder.Property(e => e.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(e => e.PayloadHash).HasMaxLength(64);
        builder.Property(e => e.BranchName).HasMaxLength(200);
        builder.Property(e => e.EnvironmentName).HasMaxLength(200);
        builder.Property(e => e.CommitHash).HasMaxLength(64);
        builder.Property(e => e.StageName).HasMaxLength(200);
        builder.Property(e => e.StepName).HasMaxLength(200);
        builder.Property(e => e.RuleSetHash).HasMaxLength(128);
        builder.Property(e => e.ErrorMessage).HasMaxLength(2000);
        builder.HasIndex(e => e.OrganizationId);
        builder.HasIndex(e => new { e.ProjectId, e.CreatedAt });
        builder.HasIndex(e => new { e.PipelineRunId, e.ScannerKey, e.ContentHash }).IsUnique();
        builder.HasIndex(e => new
        {
            e.PipelineRunId,
            e.ScannerKey,
            e.StageName,
            e.StepName,
            e.PayloadHash
        }).IsUnique();
        builder.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.PipelineRun).WithMany().HasForeignKey(e => e.PipelineRunId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(e => e.PipelineArtifact).WithMany().HasForeignKey(e => e.PipelineArtifactId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AnalysisFindingConfiguration : IEntityTypeConfiguration<AnalysisFinding>
{
    public void Configure(EntityTypeBuilder<AnalysisFinding> builder)
    {
        builder.Property(e => e.Fingerprint).HasMaxLength(64).IsRequired();
        builder.Property(e => e.RuleId).HasMaxLength(300).IsRequired();
        builder.Property(e => e.Cwe).HasMaxLength(50);
        builder.Property(e => e.Title).HasMaxLength(500).IsRequired();
        builder.Property(e => e.Message).HasMaxLength(4000).IsRequired();
        builder.Property(e => e.HelpUri).HasMaxLength(2048);
        builder.HasIndex(e => e.OrganizationId);
        builder.HasIndex(e => new { e.ProjectId, e.Fingerprint, e.FingerprintVersion }).IsUnique();
        builder.HasIndex(e => new { e.ProjectId, e.Status, e.Severity });
        builder.HasIndex(e => new { e.ProjectId, e.LastSeenAt });
        // The stamp of a run's result reads the project's latest finding change.
        builder.HasIndex(e => new { e.ProjectId, e.UpdatedAt });
        builder.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AnalysisFindingOccurrenceConfiguration : IEntityTypeConfiguration<AnalysisFindingOccurrence>
{
    public void Configure(EntityTypeBuilder<AnalysisFindingOccurrence> builder)
    {
        builder.Property(e => e.LocationHash).HasMaxLength(64).IsRequired();
        builder.Property(e => e.ToolName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.ScannerKey).HasMaxLength(100).IsRequired();
        builder.Property(e => e.RuleId).HasMaxLength(300).IsRequired();
        builder.Property(e => e.FilePath).HasMaxLength(1000);
        builder.Property(e => e.Symbol).HasMaxLength(500);
        builder.Property(e => e.Message).HasMaxLength(4000).IsRequired();
        builder.Property(e => e.BranchName).HasMaxLength(200);
        builder.Property(e => e.CommitHash).HasMaxLength(64);
        builder.HasIndex(e => new { e.AnalysisReportId, e.AnalysisFindingId, e.LocationHash }).IsUnique();
        builder.HasIndex(e => new { e.AnalysisFindingId, e.CreatedAt });
        builder.HasOne(e => e.AnalysisReport).WithMany(e => e.Occurrences).HasForeignKey(e => e.AnalysisReportId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.AnalysisFinding).WithMany(e => e.Occurrences).HasForeignKey(e => e.AnalysisFindingId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AnalysisMetricConfiguration : IEntityTypeConfiguration<AnalysisMetric>
{
    public void Configure(EntityTypeBuilder<AnalysisMetric> builder)
    {
        builder.Property(e => e.Key).HasMaxLength(300).IsRequired();
        builder.Property(e => e.Unit).HasMaxLength(50);
        builder.Property(e => e.Scope).HasMaxLength(200);
        builder.Property(e => e.Language).HasMaxLength(100);
        builder.Property(e => e.FilePath).HasMaxLength(1000);
        builder.Property(e => e.Symbol).HasMaxLength(500);
        builder.Property(e => e.ToolName).HasMaxLength(200).IsRequired();
        builder.HasIndex(e => e.OrganizationId);
        builder.HasIndex(e => new { e.ProjectId, e.Key, e.CreatedAt });
        builder.HasIndex(e => new { e.AnalysisReportId, e.Key });
        builder.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.AnalysisReport).WithMany(e => e.Metrics).HasForeignKey(e => e.AnalysisReportId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AnalysisComponentConfiguration : IEntityTypeConfiguration<AnalysisComponent>
{
    public void Configure(EntityTypeBuilder<AnalysisComponent> builder)
    {
        builder.Property(e => e.Name).HasMaxLength(500).IsRequired();
        builder.Property(e => e.Version).HasMaxLength(200).IsRequired();
        builder.Property(e => e.PackageUrl).HasMaxLength(2000);
        builder.Property(e => e.ComponentType).HasMaxLength(100);
        builder.Property(e => e.LicensesJson).HasMaxLength(4000);
        builder.Property(e => e.Hash).HasMaxLength(256);
        builder.HasIndex(e => e.OrganizationId);
        builder.HasIndex(e => new { e.ProjectId, e.PackageUrl, e.Version });
        builder.HasIndex(e => e.AnalysisReportId);
        builder.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.AnalysisReport).WithMany(e => e.Components).HasForeignKey(e => e.AnalysisReportId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AnalysisPolicyConfiguration : IEntityTypeConfiguration<AnalysisPolicy>
{
    public void Configure(EntityTypeBuilder<AnalysisPolicy> builder)
    {
        builder.Property(e => e.PolicyKey).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.Property(e => e.MetricKey).HasMaxLength(300);
        builder.Property(e => e.ScannerKey).HasMaxLength(100);
        builder.Property(e => e.RuleId).HasMaxLength(300);
        builder.Property(e => e.BranchPattern).HasMaxLength(300);
        builder.Property(e => e.EnvironmentPattern).HasMaxLength(300);
        builder.HasIndex(e => e.OrganizationId);
        builder.HasIndex(e => new { e.OrganizationId, e.ProjectId, e.PolicyKey })
            .IsUnique()
            .AreNullsDistinct(false);
        builder.HasIndex(e => new { e.OrganizationId, e.ProjectId, e.Name })
            .IsUnique()
            .AreNullsDistinct(false);
        builder.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AnalysisPolicyExceptionConfiguration : IEntityTypeConfiguration<AnalysisPolicyException>
{
    public void Configure(EntityTypeBuilder<AnalysisPolicyException> builder)
    {
        builder.Property(e => e.Fingerprint).HasMaxLength(64);
        builder.Property(e => e.RuleId).HasMaxLength(300);
        builder.Property(e => e.ScannerKey).HasMaxLength(100);
        builder.Property(e => e.BranchPattern).HasMaxLength(300);
        builder.Property(e => e.EnvironmentPattern).HasMaxLength(300);
        builder.Property(e => e.Reason).HasMaxLength(2000).IsRequired();
        builder.Property(e => e.CreatedByUsername).HasMaxLength(256).IsRequired();
        builder.HasIndex(e => e.OrganizationId);
        builder.HasIndex(e => new { e.ProjectId, e.ExpiresAt });
        builder.HasIndex(e => e.AnalysisFindingId);
        builder.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.AnalysisPolicy).WithMany(e => e.Exceptions).HasForeignKey(e => e.AnalysisPolicyId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(e => e.AnalysisFinding).WithMany().HasForeignKey(e => e.AnalysisFindingId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AnalysisPolicyRevisionConfiguration : IEntityTypeConfiguration<AnalysisPolicyRevision>
{
    public void Configure(EntityTypeBuilder<AnalysisPolicyRevision> builder)
    {
        builder.Property(e => e.SnapshotJson).HasMaxLength(100_000).IsRequired();
        builder.Property(e => e.SnapshotHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(e => new { e.AnalysisPolicyId, e.Version }).IsUnique();
        builder.HasOne(e => e.AnalysisPolicy).WithMany(e => e.Revisions)
            .HasForeignKey(e => e.AnalysisPolicyId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AnalysisFindingDecisionConfiguration : IEntityTypeConfiguration<AnalysisFindingDecision>
{
    public void Configure(EntityTypeBuilder<AnalysisFindingDecision> builder)
    {
        builder.Property(e => e.Reason).HasMaxLength(2000).IsRequired();
        builder.Property(e => e.CreatedByUsername).HasMaxLength(256).IsRequired();
        builder.HasIndex(e => e.OrganizationId);
        builder.HasIndex(e => new { e.AnalysisFindingId, e.CreatedAt });
        builder.HasIndex(e => new { e.ProjectId, e.ExpiresAt });
        builder.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.AnalysisFinding).WithMany(e => e.Decisions).HasForeignKey(e => e.AnalysisFindingId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AnalysisEvaluationConfiguration : IEntityTypeConfiguration<AnalysisEvaluation>
{
    public void Configure(EntityTypeBuilder<AnalysisEvaluation> builder)
    {
        builder.Property(e => e.PolicySnapshotJson).HasMaxLength(100_000).IsRequired();
        builder.Property(e => e.PolicySnapshotHash).HasMaxLength(64).IsRequired();
        builder.Property(e => e.GradeSnapshotJson).HasMaxLength(100_000).IsRequired();
        builder.Property(e => e.GradeSnapshotHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(e => e.OrganizationId);
        builder.HasIndex(e => e.AnalysisReportId).IsUnique();
        builder.HasIndex(e => new { e.ProjectId, e.EvaluatedAt });
        builder.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.AnalysisReport).WithOne(e => e.Evaluation).HasForeignKey<AnalysisEvaluation>(e => e.AnalysisReportId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.PipelineRun).WithMany(run => run!.AnalysisEvaluations)
            .HasForeignKey(e => e.PipelineRunId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(e => e.BaselineRun).WithMany().HasForeignKey(e => e.BaselineRunId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AnalysisTrackingProjectConfiguration : IEntityTypeConfiguration<AnalysisTrackingProject>
{
    public void Configure(EntityTypeBuilder<AnalysisTrackingProject> builder)
    {
        builder.Property(e => e.Provider).HasMaxLength(100).IsRequired();
        builder.Property(e => e.ExternalProjectId).HasMaxLength(200).IsRequired();
        builder.Property(e => e.ExternalProjectName).HasMaxLength(500).IsRequired();
        builder.Property(e => e.SyncStatus).HasMaxLength(50).IsRequired();
        builder.Property(e => e.LastError).HasMaxLength(2000);
        builder.Property(e => e.LastSnapshotHash).HasMaxLength(64);
        builder.HasIndex(e => e.OrganizationId);
        builder.HasIndex(e => e.ProjectId).IsUnique();
        builder.HasIndex(e => new { e.Active, e.LastSyncAt });
        builder.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.LastSbomReport).WithMany().HasForeignKey(e => e.LastSbomReportId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AnalysisVulnerabilityObservationConfiguration : IEntityTypeConfiguration<AnalysisVulnerabilityObservation>
{
    public void Configure(EntityTypeBuilder<AnalysisVulnerabilityObservation> builder)
    {
        builder.Property(e => e.VulnerabilityId).HasMaxLength(200).IsRequired();
        builder.Property(e => e.ComponentName).HasMaxLength(500).IsRequired();
        builder.Property(e => e.ComponentVersion).HasMaxLength(200).IsRequired();
        builder.Property(e => e.PackageUrl).HasMaxLength(2000);
        builder.Property(e => e.Status).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Source).HasMaxLength(200).IsRequired();
        builder.HasIndex(e => e.OrganizationId);
        builder.HasIndex(e => new { e.ProjectId, e.ObservedAt });
        builder.HasIndex(e => new { e.AnalysisTrackingProjectId, e.VulnerabilityId, e.PackageUrl, e.ObservedAt });
        builder.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Project).WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.AnalysisTrackingProject).WithMany(e => e.VulnerabilityObservations).HasForeignKey(e => e.AnalysisTrackingProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.AnalysisReport).WithMany().HasForeignKey(e => e.AnalysisReportId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class DastExecutionLeaseConfiguration : IEntityTypeConfiguration<DastExecutionLease>
{
    public void Configure(EntityTypeBuilder<DastExecutionLease> builder)
    {
        builder.Property(item => item.Token).HasMaxLength(64).IsRequired();
        builder.Property(item => item.TargetHost).HasMaxLength(253).IsRequired();
        builder.HasIndex(item => item.Token).IsUnique();
        builder.HasIndex(item => new { item.PipelineRunId, item.ExpiresAt });
        builder.HasIndex(item => new { item.EnvironmentId, item.TargetHost, item.TargetPort });
        builder.HasOne(item => item.PipelineRun).WithMany()
            .HasForeignKey(item => item.PipelineRunId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.PipelineStepRun).WithMany()
            .HasForeignKey(item => item.PipelineStepRunId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Environment).WithMany()
            .HasForeignKey(item => item.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DependencyTrackOutboxItemConfiguration : IEntityTypeConfiguration<DependencyTrackOutboxItem>
{
    public void Configure(EntityTypeBuilder<DependencyTrackOutboxItem> builder)
    {
        builder.Property(item => item.ExternalProjectName).HasMaxLength(500).IsRequired();
        builder.Property(item => item.ProjectVersion).HasMaxLength(128).IsRequired();
        builder.Property(item => item.ReportEntryPath).HasMaxLength(1000).IsRequired();
        builder.Property(item => item.Status).HasMaxLength(32).IsRequired();
        builder.Property(item => item.LastError).HasMaxLength(2000);
        builder.HasIndex(item => item.AnalysisReportId).IsUnique();
        builder.HasIndex(item => new { item.Status, item.NextAttemptAt });
        builder.HasIndex(item => new { item.ProjectId, item.CreatedAt });
        builder.HasOne(item => item.AnalysisReport).WithMany()
            .HasForeignKey(item => item.AnalysisReportId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.PipelineArtifact).WithMany()
            .HasForeignKey(item => item.PipelineArtifactId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(item => item.Organization).WithMany()
            .HasForeignKey(item => item.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(item => item.Project).WithMany()
            .HasForeignKey(item => item.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}
