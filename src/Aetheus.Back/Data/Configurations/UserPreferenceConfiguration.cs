// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class UserPreferenceConfiguration : IEntityTypeConfiguration<UserPreference>
{
    public void Configure(EntityTypeBuilder<UserPreference> builder)
    {
        builder.Property(p => p.Key).HasMaxLength(UserPreferenceRules.MaxKeyLength).IsRequired();
        builder.Property(p => p.Value).HasMaxLength(UserPreferenceRules.MaxValueLength);
        // One row per key and user; the synchronization reads a user's rows changed since a date.
        builder.HasIndex(p => new { p.UserId, p.Key }).IsUnique();
        builder.HasIndex(p => new { p.UserId, p.ChangedAt });
        builder.HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
