// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class ApacheStateConfiguration : IEntityTypeConfiguration<ApacheState>
{
    public void Configure(EntityTypeBuilder<ApacheState> builder)
    {
        builder.HasOne(e => e.Server)
               .WithOne(s => s.ApacheState)
               .HasForeignKey<ApacheState>(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ApacheModuleConfiguration : IEntityTypeConfiguration<ApacheModule>
{
    public void Configure(EntityTypeBuilder<ApacheModule> builder)
    {
        builder.HasOne(e => e.Server)
               .WithMany(s => s.ApacheModules)
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ApacheVirtualHostConfiguration : IEntityTypeConfiguration<ApacheVirtualHost>
{
    public void Configure(EntityTypeBuilder<ApacheVirtualHost> builder)
    {
        builder.HasOne(e => e.Server)
               .WithMany(s => s.ApacheVirtualHosts)
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
