// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class GitBranchConfiguration : IEntityTypeConfiguration<GitBranch>
{
    public void Configure(EntityTypeBuilder<GitBranch> builder)
    {
        builder.Property(e => e.Name).HasMaxLength(200);
        builder.HasIndex(e => new { e.ProjectId, e.Name }).IsUnique();
        builder.HasOne(e => e.Project)
               .WithMany()
               .HasForeignKey(e => e.ProjectId)
               .OnDelete(DeleteBehavior.Cascade);

        // Branch ↔ Release / Artifact / Commit (many-to-many, implicit join tables).
        builder.HasMany(b => b.Releases)
               .WithMany(r => r.Branches)
               .UsingEntity(j => j.ToTable("BranchReleases"));
        builder.HasMany(b => b.Artifacts)
               .WithMany(a => a.Branches)
               .UsingEntity(j => j.ToTable("BranchArtifacts"));
        builder.HasMany(b => b.Commits)
               .WithMany(c => c.Branches)
               .UsingEntity(j => j.ToTable("BranchCommits"));
    }
}

internal sealed class GitCommitConfiguration : IEntityTypeConfiguration<GitCommit>
{
    public void Configure(EntityTypeBuilder<GitCommit> builder)
    {
        builder.Property(e => e.Sha).HasMaxLength(64);
        builder.Property(e => e.Message).HasMaxLength(2000);
        builder.Property(e => e.Author).HasMaxLength(200);
        builder.HasIndex(e => new { e.ProjectId, e.Sha }).IsUnique();
        // The projects list reads each project's newest commit by date (CommittedAt when known,
        // else CreatedAt) as two top-1 lookups, one per index, instead of sorting every commit.
        builder.HasIndex(e => new { e.ProjectId, e.CommittedAt, e.Id });
        builder.HasIndex(e => new { e.ProjectId, e.CreatedAt, e.Id });
        builder.HasOne(e => e.Project)
               .WithMany()
               .HasForeignKey(e => e.ProjectId)
               .OnDelete(DeleteBehavior.Cascade);

        // Commit ↔ Release / Artifact (many-to-many, implicit join tables).
        // Commit ↔ Branch is configured on the GitBranch side.
        builder.HasMany(c => c.Releases)
               .WithMany(r => r.Commits)
               .UsingEntity(j => j.ToTable("CommitReleases"));
        builder.HasMany(c => c.Artifacts)
               .WithMany(a => a.Commits)
               .UsingEntity(j => j.ToTable("CommitArtifacts"));
    }
}

internal sealed class ReleaseArtifactLinkConfiguration : IEntityTypeConfiguration<Release>
{
    public void Configure(EntityTypeBuilder<Release> builder)
    {
        // Release ↔ Artifact (many-to-many, implicit join table). Replaces the former one-to-one.
        builder.HasMany(r => r.Artifacts)
               .WithMany(a => a.Releases)
               .UsingEntity(j => j.ToTable("ReleaseArtifacts"));
    }
}
