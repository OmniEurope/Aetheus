// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.Property(d => d.EventType).HasMaxLength(100).IsRequired();
        builder.Property(d => d.Subject).HasMaxLength(300).IsRequired();
        builder.Property(d => d.PayloadJson).IsRequired();
        builder.Property(d => d.ErrorMessage).HasMaxLength(2000);
        // The user's list reads newest first; the unread counter filters on ReadAt.
        builder.HasIndex(d => new { d.RecipientUserId, d.CreatedAt }).IsDescending(false, true);
        builder.HasIndex(d => new { d.RecipientUserId, d.ReadAt });
        builder.HasOne(d => d.Recipient)
            .WithMany()
            .HasForeignKey(d => d.RecipientUserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(d => d.Channel)
            .WithMany()
            .HasForeignKey(d => d.ChannelId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class ProjectSubscriptionConfiguration : IEntityTypeConfiguration<ProjectSubscription>
{
    public void Configure(EntityTypeBuilder<ProjectSubscription> builder)
    {
        builder.HasKey(s => new { s.UserId, s.ProjectId });
        builder.HasIndex(s => s.ProjectId);
        builder.HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(s => s.Project)
            .WithMany()
            .HasForeignKey(s => s.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserPipelineNotificationPreferenceConfiguration : IEntityTypeConfiguration<UserPipelineNotificationPreference>
{
    public void Configure(EntityTypeBuilder<UserPipelineNotificationPreference> builder)
    {
        builder.HasKey(p => new { p.UserId, p.PipelineId, p.EventType });
        builder.Property(p => p.EventType).HasMaxLength(100).IsRequired();
        // Dispatch reads the choices of one pipeline's recipients; deleting a pipeline finds its rows by it.
        builder.HasIndex(p => new { p.PipelineId, p.EventType });
        builder.HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(p => p.Pipeline)
            .WithMany()
            .HasForeignKey(p => p.PipelineId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserNotificationPreferenceConfiguration : IEntityTypeConfiguration<UserNotificationPreference>
{
    public void Configure(EntityTypeBuilder<UserNotificationPreference> builder)
    {
        builder.Property(p => p.EventType).HasMaxLength(100).IsRequired();
        builder.HasIndex(p => new { p.UserId, p.EventType }).IsUnique();
        builder.HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
