// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class MailTemplateConfiguration : IEntityTypeConfiguration<MailTemplate>
{
    public void Configure(EntityTypeBuilder<MailTemplate> builder)
    {
        builder.Property(t => t.Kind).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(t => t.Language).HasMaxLength(8).IsRequired();
        builder.Property(t => t.Subject).HasMaxLength(MailTemplateLimits.MaxSubjectLength).IsRequired();
        builder.Property(t => t.Body).HasMaxLength(MailTemplateLimits.MaxBodyLength).IsRequired();
        builder.Property(t => t.UpdatedBy).HasMaxLength(200);
        // One template per kind and language.
        builder.HasIndex(t => new { t.Kind, t.Language }).IsUnique();
    }
}

internal sealed class OrganizationMailTemplateConfiguration : IEntityTypeConfiguration<OrganizationMailTemplate>
{
    public void Configure(EntityTypeBuilder<OrganizationMailTemplate> builder)
    {
        builder.Property(t => t.Kind).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(t => t.Language).HasMaxLength(8).IsRequired();
        builder.Property(t => t.Subject).HasMaxLength(MailTemplateLimits.MaxSubjectLength).IsRequired();
        builder.Property(t => t.Body).HasMaxLength(MailTemplateLimits.MaxBodyLength).IsRequired();
        builder.Property(t => t.UpdatedBy).HasMaxLength(200);
        // One version per organization, kind and language; the organization's versions go with it.
        builder.HasIndex(t => new { t.OrganizationId, t.Kind, t.Language }).IsUnique();
        builder.HasOne(t => t.Organization).WithMany().HasForeignKey(t => t.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}
