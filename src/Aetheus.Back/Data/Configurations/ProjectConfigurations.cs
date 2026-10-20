// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.HasIndex(e => e.Name).IsUnique();
        builder.HasIndex(e => e.OrganizationId);
        builder.HasOne(e => e.Organization)
               .WithMany(o => o.Projects)
               .HasForeignKey(e => e.OrganizationId)
               .OnDelete(DeleteBehavior.Restrict);

        // External-Git source discriminant. Distinct from the GitConnection -> Project (cascade) FK:
        // this is the back-reference marking the project's active external source. NoAction avoids a
        // multiple-cascade-path cycle between Project and GitConnection; the service nulls it before
        // detaching a source connection.
        builder.HasOne(e => e.GitConnection)
               .WithMany()
               .HasForeignKey(e => e.GitConnectionId)
               .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ProjectServerConfiguration : IEntityTypeConfiguration<ProjectServer>
{
    public void Configure(EntityTypeBuilder<ProjectServer> builder)
    {
        builder.HasIndex(e => e.ProjectId);
        builder.HasIndex(e => new { e.ProjectId, e.ServerId })
               .IsUnique()
               .HasFilter("\"ServerId\" IS NOT NULL");
        builder.Property(e => e.DisplayName).HasMaxLength(200);
        builder.Property(e => e.Host).HasMaxLength(500);
        builder.Property(e => e.Notes).HasMaxLength(2000);
        builder.HasOne(e => e.Project)
               .WithMany(p => p.ProjectServers)
               .HasForeignKey(e => e.ProjectId)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Server)
               .WithMany(s => s.ProjectServers)
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReleaseConfiguration : IEntityTypeConfiguration<Release>
{
    public void Configure(EntityTypeBuilder<Release> builder)
    {
        builder.HasIndex(e => new { e.ProjectId, e.Version }).IsUnique();
        builder.HasIndex(e => e.ProjectId)
               .IsUnique()
               .HasFilter("\"Status\" = 6");
        builder.HasIndex(e => e.PipelineRunId);
        // Release lists sort by PublishedAt DESC (kept sargable in ReleaseRepository).
        builder.HasIndex(e => e.PublishedAt);
        builder.HasOne(e => e.Project)
               .WithMany()
               .HasForeignKey(e => e.ProjectId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.PipelineRun)
               .WithMany()
               .HasForeignKey(e => e.PipelineRunId)
               .OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(e => e.CreatedByPipelineRunId);
        builder.HasOne(e => e.CreatedByPipelineRun)
               .WithMany()
               .HasForeignKey(e => e.CreatedByPipelineRunId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class ReleaseRollbackConfiguration : IEntityTypeConfiguration<ReleaseRollback>
{
    public void Configure(EntityTypeBuilder<ReleaseRollback> builder)
    {
        builder.HasIndex(e => e.PipelineRunId).IsUnique().HasFilter("\"PipelineRunId\" IS NOT NULL");
        builder.HasIndex(e => new { e.SourceReleaseId, e.RequestedAt });
        builder.Property(e => e.FailureReason).HasMaxLength(2000);
        builder.HasOne(e => e.SourceRelease).WithMany(e => e.RollbacksFrom)
            .HasForeignKey(e => e.SourceReleaseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.TargetRelease).WithMany(e => e.RollbacksTo)
            .HasForeignKey(e => e.TargetReleaseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Pipeline).WithMany().HasForeignKey(e => e.PipelineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.PipelineRun).WithMany().HasForeignKey(e => e.PipelineRunId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class ProjectReleaseSeriesConfiguration : IEntityTypeConfiguration<ProjectReleaseSeries>
{
    public void Configure(EntityTypeBuilder<ProjectReleaseSeries> builder)
    {
        builder.Property(e => e.Series).HasMaxLength(ReleaseSeries.MaxLength);
        builder.HasIndex(e => new { e.ProjectId, e.Series }).IsUnique();
        builder.HasOne(e => e.Project)
               .WithMany()
               .HasForeignKey(e => e.ProjectId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProjectReleaseNumberAllocationConfiguration : IEntityTypeConfiguration<ProjectReleaseNumberAllocation>
{
    public void Configure(EntityTypeBuilder<ProjectReleaseNumberAllocation> builder)
    {
        builder.Property(e => e.Series).HasMaxLength(ReleaseSeries.MaxLength);
        builder.HasIndex(e => new { e.ProjectId, e.Series, e.Number }).IsUnique();
        builder.HasOne(e => e.Project)
               .WithMany()
               .HasForeignKey(e => e.ProjectId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
