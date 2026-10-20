// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class WorkItemConfiguration : IEntityTypeConfiguration<WorkItem>
{
    public void Configure(EntityTypeBuilder<WorkItem> builder)
    {
        builder.Property(w => w.Title).HasMaxLength(300).IsRequired();
        builder.Property(w => w.Description).HasMaxLength(4000);
        builder.Property(w => w.Tags).HasMaxLength(1000);
        builder.Property(w => w.ExternalId).HasMaxLength(100);
        builder.Property(w => w.ExternalUrl).HasMaxLength(500);
        // The findings grid reads, for each page, the work items of "analysis:{id}" in one project.
        // The foreign-key index on ProjectId is declared too, so EF keeps it rather than dropping
        // it in favour of the composite: the blue-green migration gate refuses a DropIndex (not
        // expand-safe).
        builder.HasIndex(w => w.ProjectId);
        builder.HasIndex(w => new { w.ProjectId, w.ExternalId });
        builder.HasOne(w => w.Project)
            .WithMany()
            .HasForeignKey(w => w.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(w => w.Assignee)
            .WithMany()
            .HasForeignKey(w => w.AssigneeUserId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(w => w.Parent)
            .WithMany(w => w.Children)
            .HasForeignKey(w => w.ParentId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(w => w.LinkedPipelineRun)
            .WithMany()
            .HasForeignKey(w => w.LinkedPipelineRunId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
