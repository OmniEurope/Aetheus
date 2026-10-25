// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class GitInternalRepoConfiguration : IEntityTypeConfiguration<GitInternalRepo>
{
    public void Configure(EntityTypeBuilder<GitInternalRepo> builder)
    {
        builder.HasIndex(r => new { r.ProjectId, r.Slug }).IsUnique();
        builder.Property(r => r.Name).HasMaxLength(200).IsRequired();
        builder.Property(r => r.Slug).HasMaxLength(200).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(1000);
        builder.Property(r => r.DefaultBranch).HasMaxLength(200).IsRequired();
        builder.Property(r => r.IsConfiguration).HasDefaultValue(false);
        builder.HasOne(r => r.Project)
            .WithMany()
            .HasForeignKey(r => r.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(r => r.GitConnection)
            .WithMany()
            .HasForeignKey(r => r.GitConnectionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class BranchProtectionRuleConfiguration : IEntityTypeConfiguration<BranchProtectionRule>
{
    public void Configure(EntityTypeBuilder<BranchProtectionRule> builder)
    {
        builder.HasIndex(r => new { r.GitInternalRepoId, r.Pattern }).IsUnique();
        builder.Property(r => r.Pattern).HasMaxLength(200).IsRequired();
        builder.HasOne(r => r.Repo)
            .WithMany()
            .HasForeignKey(r => r.GitInternalRepoId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
