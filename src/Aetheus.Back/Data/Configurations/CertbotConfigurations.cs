// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class CertbotCertificateConfiguration : IEntityTypeConfiguration<CertbotCertificate>
{
    public void Configure(EntityTypeBuilder<CertbotCertificate> builder)
    {
        builder.Property(e => e.Authenticator).HasMaxLength(100);
        builder.Property(e => e.WebrootPath).HasMaxLength(4096);
        builder.HasIndex(e => new { e.ServerId, e.Name }).IsUnique();
        builder.HasOne(e => e.Server)
               .WithMany(s => s.CertbotCertificates)
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CertificateExpiryAlertMarkConfiguration : IEntityTypeConfiguration<CertificateExpiryAlertMark>
{
    public void Configure(EntityTypeBuilder<CertificateExpiryAlertMark> builder)
    {
        builder.Property(e => e.CertificateName).HasMaxLength(255);
        builder.HasIndex(e => new { e.ServerId, e.CertificateName, e.ExpiryDate, e.LeadDays }).IsUnique();
        builder.HasOne(e => e.Server)
               .WithMany()
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CertbotStateConfiguration : IEntityTypeConfiguration<CertbotState>
{
    public void Configure(EntityTypeBuilder<CertbotState> builder)
    {
        builder.HasIndex(e => e.ServerId).IsUnique();
        builder.HasOne(e => e.Server)
               .WithOne(s => s.CertbotState)
               .HasForeignKey<CertbotState>(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
