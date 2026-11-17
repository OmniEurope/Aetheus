// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class ServerRebootScheduleConfiguration : IEntityTypeConfiguration<ServerRebootSchedule>
{
    public void Configure(EntityTypeBuilder<ServerRebootSchedule> builder)
    {
        builder.HasIndex(e => e.ServerId).IsUnique();
        builder.Property(e => e.TimeZoneId).HasMaxLength(64);
        builder.Property(e => e.UpdatedBy).HasMaxLength(256);
        builder.Property(e => e.OutcomeDetail).HasMaxLength(200);
        builder.HasOne(e => e.Server)
               .WithOne(s => s.RebootSchedule)
               .HasForeignKey<ServerRebootSchedule>(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
