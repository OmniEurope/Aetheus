// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class RkhunterStateConfiguration : IEntityTypeConfiguration<RkhunterState>
{
    public void Configure(EntityTypeBuilder<RkhunterState> builder)
    {
        builder.HasIndex(e => e.ServerId).IsUnique();
        builder.HasOne(e => e.Server)
               .WithOne(s => s.RkhunterState)
               .HasForeignKey<RkhunterState>(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RkhunterWarningConfiguration : IEntityTypeConfiguration<RkhunterWarning>
{
    public void Configure(EntityTypeBuilder<RkhunterWarning> builder)
    {
        builder.HasIndex(e => e.ServerId);
        builder.HasOne(e => e.Server)
               .WithMany(s => s.RkhunterWarnings)
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RkhunterScanResultConfiguration : IEntityTypeConfiguration<RkhunterScanResult>
{
    public void Configure(EntityTypeBuilder<RkhunterScanResult> builder)
    {
        builder.HasOne(e => e.Server)
               .WithMany(s => s.RkhunterScanResults)
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
