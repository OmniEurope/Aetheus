// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class SystemLogExclusionRuleConfiguration : IEntityTypeConfiguration<SystemLogExclusionRule>
{
    public void Configure(EntityTypeBuilder<SystemLogExclusionRule> builder)
    {
        builder.Property(rule => rule.Pattern).HasMaxLength(SaveSystemLogExclusionRuleRequest.MaxPatternLength).IsRequired();
        builder.Property(rule => rule.Category).HasMaxLength(SaveSystemLogExclusionRuleRequest.MaxCategoryLength).IsRequired();
        builder.Property(rule => rule.CreatedBy).HasMaxLength(200);
        // One rule per organization, pattern and category; an organization's rules go with it.
        builder.HasIndex(rule => new { rule.OrganizationId, rule.Pattern, rule.Category }).IsUnique();
        builder.HasOne(rule => rule.Organization).WithMany().HasForeignKey(rule => rule.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}
