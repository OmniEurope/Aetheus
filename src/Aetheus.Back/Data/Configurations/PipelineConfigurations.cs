// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class PipelineConfiguration : IEntityTypeConfiguration<Pipeline>
{
    public void Configure(EntityTypeBuilder<Pipeline> builder)
    {
        // Per-owner name uniqueness via partial unique indexes (was global). Required so an
        // environment duplicated into another project can carry pipelines with the same name.
        builder.HasIndex(e => new { e.Name, e.ProjectId }).IsUnique().HasFilter("\"ProjectId\" IS NOT NULL");
        builder.HasIndex(e => new { e.Name, e.EnvironmentId }).IsUnique().HasFilter("\"EnvironmentId\" IS NOT NULL");
        builder.HasIndex(e => new { e.Name, e.ProjectServerId }).IsUnique().HasFilter("\"ProjectServerId\" IS NOT NULL");
        builder.Property(e => e.SourceBranch).HasMaxLength(255);
        builder.Property(e => e.TemplateReferenceName).HasMaxLength(100);
        builder.HasIndex(e => new { e.TemplateReferenceName, e.TemplateReferenceVersion });
        builder.HasOne(e => e.SourceRepository)
               .WithMany()
               .HasForeignKey(e => e.SourceRepositoryId)
               .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(e => e.Project)
               .WithMany(p => p.Pipelines)
               .HasForeignKey(e => e.ProjectId)
               .OnDelete(DeleteBehavior.SetNull);
        // SetNull (not Cascade) on env/server delete: keep the pipeline + its run history.
        builder.HasOne(e => e.Environment)
               .WithMany(env => env.Pipelines)
               .HasForeignKey(e => e.EnvironmentId)
               .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(e => e.ProjectServer)
               .WithMany(ps => ps.Pipelines)
               .HasForeignKey(e => e.ProjectServerId)
               .OnDelete(DeleteBehavior.SetNull);
        // Nullable and SetNull: deleting a profile only returns its pipelines to automatic selection.
        builder.HasOne(e => e.AiRunnerProfile)
               .WithMany()
               .HasForeignKey(e => e.AiRunnerProfileId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PipelineRunConfiguration : IEntityTypeConfiguration<PipelineRun>
{
    public void Configure(EntityTypeBuilder<PipelineRun> builder)
    {
        builder.HasIndex(e => e.PipelineId);
        builder.HasIndex(e => e.Status);
        builder.HasIndex(e => e.StartedAt); // F-041: retention service filter
        builder.HasIndex(e => new { e.PipelineId, e.Status });
        builder.Property(e => e.BranchName).HasMaxLength(200);
        builder.Property(e => e.CommitHash).HasMaxLength(64);
        builder.Property(e => e.RepositoryUrl).HasMaxLength(2048);
        builder.Property(e => e.ReleaseSeries).HasMaxLength(ReleaseSeries.MaxLength);
        builder.Property(e => e.IdempotencyKey).HasMaxLength(PipelineRunRequest.MaxIdempotencyKeyLength);
        builder.HasIndex(e => new { e.PipelineId, e.IdempotencyKey })
               .IsUnique()
               .HasFilter("\"IdempotencyKey\" IS NOT NULL");
        builder.HasOne(e => e.Pipeline)
               .WithMany(p => p.Runs)
               .HasForeignKey(e => e.PipelineId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PipelineStepRunConfiguration : IEntityTypeConfiguration<PipelineStepRun>
{
    public void Configure(EntityTypeBuilder<PipelineStepRun> builder)
    {
        builder.HasIndex(e => e.PipelineRunId);
        builder.HasIndex(e => e.ServerId);
        builder.HasIndex(e => e.TriggeredRunId);
        builder.HasIndex(e => new { e.PipelineRunId, e.Status });
        builder.HasIndex(e => new { e.PipelineRunId, e.StageName });
        builder.Property(e => e.FailureCode).HasMaxLength(64);
        builder.Property(e => e.FailureReason).HasMaxLength(2048);
        builder.Property(e => e.SkippedCondition).HasMaxLength(1000);
        builder.Property(e => e.SkippedConditionVariablesJson).HasMaxLength(4000);
        builder.Property(e => e.SkippedReason).HasMaxLength(1000);
        builder.HasOne(e => e.PipelineRun)
               .WithMany(r => r.StepRuns)
               .HasForeignKey(e => e.PipelineRunId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Server)
               .WithMany()
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(e => e.Task)
               .WithMany()
               .HasForeignKey(e => e.TaskId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PipelineRunArtifactInputConfiguration : IEntityTypeConfiguration<PipelineRunArtifactInput>
{
    public void Configure(EntityTypeBuilder<PipelineRunArtifactInput> builder)
    {
        // Unbounded like PipelineStepRun.StepName and PipelineArtifact.Name, which these copy.
        builder.Property(e => e.Sha256).HasMaxLength(64);
        builder.HasIndex(e => e.PipelineRunId);
        builder.HasIndex(e => e.ArtifactId);
        builder.HasIndex(e => e.ReleaseId);
        // The record lives and dies with its run; losing the artifact or the release keeps the copied
        // name and digest readable instead of erasing the fact that they were consumed.
        builder.HasOne(e => e.PipelineRun)
               .WithMany()
               .HasForeignKey(e => e.PipelineRunId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Artifact)
               .WithMany()
               .HasForeignKey(e => e.ArtifactId)
               .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(e => e.Release)
               .WithMany()
               .HasForeignKey(e => e.ReleaseId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PipelineMigrationGateVerdictConfiguration : IEntityTypeConfiguration<PipelineMigrationGateVerdict>
{
    public void Configure(EntityTypeBuilder<PipelineMigrationGateVerdict> builder)
    {
        builder.HasIndex(e => e.PipelineRunId);
        builder.Property(e => e.Detail).HasMaxLength(MigrationGateReport.MaxTextLength);
        builder.HasOne(e => e.PipelineRun)
               .WithMany(r => r.MigrationGateVerdicts)
               .HasForeignKey(e => e.PipelineRunId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
