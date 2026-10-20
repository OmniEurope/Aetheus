// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Data.Configurations;

internal sealed class EnvironmentConfiguration : IEntityTypeConfiguration<Environment>
{
    public void Configure(EntityTypeBuilder<Environment> builder)
    {
        // Per-project name uniqueness (was global): lets the same environment name (e.g. "Staging")
        // exist across projects and supports environment duplication into another project.
        builder.HasIndex(e => new { e.Name, e.ProjectId }).IsUnique();
        builder.HasOne(e => e.Project)
               .WithMany()
               .HasForeignKey(e => e.ProjectId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class EnvironmentProjectServerConfiguration : IEntityTypeConfiguration<EnvironmentProjectServer>
{
    public void Configure(EntityTypeBuilder<EnvironmentProjectServer> builder)
    {
        builder.HasKey(e => new { e.EnvironmentId, e.ProjectServerId });
        builder.HasOne(e => e.Environment)
               .WithMany(env => env.LinkedProjectServers)
               .HasForeignKey(e => e.EnvironmentId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.ProjectServer)
               .WithMany(ps => ps.LinkedEnvironments)
               .HasForeignKey(e => e.ProjectServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class EnvironmentServerConfiguration : IEntityTypeConfiguration<EnvironmentServer>
{
    public void Configure(EntityTypeBuilder<EnvironmentServer> builder)
    {
        builder.HasKey(e => new { e.EnvironmentId, e.ServerId });
        // Hide retained retired-server links so environment link replacement does not delete them
        // and revival restores membership.
        builder.HasQueryFilter(ServerQueryFilters.ExcludeRetired, e => e.Server.DeletedAt == null);
        builder.HasOne(e => e.Environment)
               .WithMany(env => env.Servers)
               .HasForeignKey(e => e.EnvironmentId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Server)
               .WithMany()
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PipelineApprovalConfiguration : IEntityTypeConfiguration<PipelineApproval>
{
    public void Configure(EntityTypeBuilder<PipelineApproval> builder)
    {
        builder.Property(e => e.Comments).HasMaxLength(2000);
        builder.HasIndex(e => new { e.PipelineRunId, e.StageName });
        builder.HasOne(e => e.PipelineRun)
               .WithMany()
               .HasForeignKey(e => e.PipelineRunId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Environment)
               .WithMany(env => env.Approvals)
               .HasForeignKey(e => e.EnvironmentId)
               .IsRequired(false)
               .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.ResolvedByUser)
               .WithMany()
               .HasForeignKey(e => e.ResolvedByUserId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AgentPoolConfiguration : IEntityTypeConfiguration<AgentPool>
{
    public void Configure(EntityTypeBuilder<AgentPool> builder)
    {
        builder.HasIndex(e => e.Name).IsUnique();
    }
}

internal sealed class AgentPoolServerConfiguration : IEntityTypeConfiguration<AgentPoolServer>
{
    public void Configure(EntityTypeBuilder<AgentPoolServer> builder)
    {
        builder.HasKey(e => new { e.AgentPoolId, e.ServerId });
        // Hide retained retired-server pool links so pool edits preserve them.
        builder.HasQueryFilter(ServerQueryFilters.ExcludeRetired, e => e.Server.DeletedAt == null);
        builder.HasOne(e => e.AgentPool)
               .WithMany(p => p.Servers)
               .HasForeignKey(e => e.AgentPoolId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Server)
               .WithMany()
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PipelineArtifactConfiguration : IEntityTypeConfiguration<PipelineArtifact>
{
    public void Configure(EntityTypeBuilder<PipelineArtifact> builder)
    {
        builder.HasIndex(e => e.PipelineRunId);
        builder.HasIndex(e => new { e.ProjectId, e.PipelineId, e.RetentionPolicy });
        builder.HasIndex(e => e.RetentionExpiresAt);
        builder.Property(e => e.Sha256).HasMaxLength(64);
        // Bind the inverse PipelineRun.Artifacts navigation explicitly. Without it EF mapped the
        // navigation as a second relationship and created a shadow FK column (PipelineRunId1);
        // binding it here collapses both onto the single PipelineRunId FK.
        builder.HasOne(e => e.PipelineRun)
               .WithMany(r => r.Artifacts)
               .HasForeignKey(e => e.PipelineRunId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Pipeline)
               .WithMany()
               .HasForeignKey(e => e.PipelineId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Project)
               .WithMany()
               .HasForeignKey(e => e.ProjectId)
               .OnDelete(DeleteBehavior.SetNull);
        // Release ↔ Artifact is many-to-many (see GitGraphConfigurations).
    }
}

internal sealed class EnvironmentCheckConfiguration : IEntityTypeConfiguration<EnvironmentCheck>
{
    public void Configure(EntityTypeBuilder<EnvironmentCheck> builder)
    {
        builder.HasIndex(e => e.EnvironmentId);
        builder.HasOne(e => e.Environment)
               .WithMany(env => env.Checks)
               .HasForeignKey(e => e.EnvironmentId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TestResultConfiguration : IEntityTypeConfiguration<TestResult>
{
    public void Configure(EntityTypeBuilder<TestResult> builder)
    {
        builder.HasIndex(e => e.PipelineRunId);
        builder.HasOne(e => e.PipelineRun)
               .WithMany(r => r.TestResults)
               .HasForeignKey(e => e.PipelineRunId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
