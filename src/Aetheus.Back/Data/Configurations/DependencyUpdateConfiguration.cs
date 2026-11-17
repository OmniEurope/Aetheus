// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class DependencyUpdateConfiguration : IEntityTypeConfiguration<DependencyUpdate>
{
    public void Configure(EntityTypeBuilder<DependencyUpdate> builder)
    {
        builder.Property(entity => entity.Ecosystem).HasMaxLength(64);
        builder.Property(entity => entity.PackageName).HasMaxLength(256);
        builder.Property(entity => entity.FromVersion).HasMaxLength(256);
        builder.Property(entity => entity.TargetVersion).HasMaxLength(256);
        builder.Property(entity => entity.Advisory).HasMaxLength(256);
        builder.Property(entity => entity.Reason).HasMaxLength(2048);
        builder.Property(entity => entity.IsMajorUpgrade).HasDefaultValue(false);
        builder.Property(entity => entity.License).HasMaxLength(512);
        builder.Property(entity => entity.Branch).HasMaxLength(256);
        builder.Property(entity => entity.CommitSha).HasMaxLength(64);
        builder.Property(entity => entity.LockRefreshOwner).HasMaxLength(256);
        builder.Property(entity => entity.LockFilesJson).HasMaxLength(4096);
        builder.HasIndex(entity => entity.LockPipelineRunId);
        // PLAN-007: one update per repository, package and target version, so a pass never writes a second branch for it.
        builder.HasIndex(entity => new { entity.GitInternalRepoId, entity.Ecosystem, entity.PackageName, entity.TargetVersion })
            .IsUnique();
        builder.HasIndex(entity => entity.ProjectId);
        builder.HasIndex(entity => entity.OrganizationId);
        builder.HasIndex(entity => entity.VulnerabilityMatchId);
        builder.HasOne(entity => entity.Organization).WithMany().HasForeignKey(entity => entity.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(entity => entity.Project).WithMany().HasForeignKey(entity => entity.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(entity => entity.GitInternalRepo).WithMany().HasForeignKey(entity => entity.GitInternalRepoId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(entity => entity.VulnerabilityMatch).WithMany().HasForeignKey(entity => entity.VulnerabilityMatchId).OnDelete(DeleteBehavior.SetNull);
    }
}
