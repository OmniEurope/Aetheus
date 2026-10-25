// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class PortsentryStateConfiguration : IEntityTypeConfiguration<PortsentryState>
{
    public void Configure(EntityTypeBuilder<PortsentryState> builder)
    {
        builder.HasIndex(e => e.ServerId).IsUnique();
        builder.HasOne(e => e.Server)
               .WithOne(s => s.PortsentryState)
               .HasForeignKey<PortsentryState>(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PortsentryBlockedIpConfiguration : IEntityTypeConfiguration<PortsentryBlockedIp>
{
    public void Configure(EntityTypeBuilder<PortsentryBlockedIp> builder)
    {
        builder.Property(e => e.IpAddress).HasMaxLength(45);
        builder.Property(e => e.Protocol).HasMaxLength(16);
        builder.Property(e => e.Reason).HasMaxLength(500);
        builder.HasIndex(e => new { e.ServerId, e.IpAddress });
        builder.HasOne(e => e.Server)
               .WithMany(s => s.PortsentryBlockedIps)
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PortsentryWhitelistIpConfiguration : IEntityTypeConfiguration<PortsentryWhitelistIp>
{
    public void Configure(EntityTypeBuilder<PortsentryWhitelistIp> builder)
    {
        builder.Property(e => e.IpAddress).HasMaxLength(45);
        builder.Property(e => e.Description).HasMaxLength(500);
        builder.HasOne(e => e.Server)
               .WithMany(s => s.PortsentryWhitelistIps)
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
