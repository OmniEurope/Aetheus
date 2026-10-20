// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class RegistryPackageConfiguration : IEntityTypeConfiguration<RegistryPackage>
{
    public void Configure(EntityTypeBuilder<RegistryPackage> builder)
    {
        builder.HasIndex(package => new { package.Kind, package.NormalizedName }).IsUnique();
        builder.Property(package => package.Name).HasMaxLength(300).IsRequired();
        builder.Property(package => package.NormalizedName).HasMaxLength(300).IsRequired();
        builder.Property(package => package.Description).HasMaxLength(1000);
        builder.Property(package => package.DistTagsJson).HasColumnType("text");
    }
}

internal sealed class RegistryPackageVersionConfiguration : IEntityTypeConfiguration<RegistryPackageVersion>
{
    public void Configure(EntityTypeBuilder<RegistryPackageVersion> builder)
    {
        builder.HasIndex(version => new { version.RegistryPackageId, version.NormalizedVersion }).IsUnique();
        builder.Property(version => version.Version).HasMaxLength(100).IsRequired();
        builder.Property(version => version.NormalizedVersion).HasMaxLength(100).IsRequired();
        builder.Property(version => version.FilePath).HasMaxLength(500).IsRequired();
        builder.Property(version => version.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(version => version.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(version => version.Sha1).HasMaxLength(40).IsRequired();
        builder.Property(version => version.Integrity).HasMaxLength(128).IsRequired();
        builder.Property(version => version.Metadata).HasColumnType("text").IsRequired();
        builder.Property(version => version.PublishedBy).HasMaxLength(100).IsRequired();
        builder.HasOne(version => version.RegistryPackage)
            .WithMany(package => package.Versions)
            .HasForeignKey(version => version.RegistryPackageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RegistryPackageFileConfiguration : IEntityTypeConfiguration<RegistryPackageFile>
{
    public void Configure(EntityTypeBuilder<RegistryPackageFile> builder)
    {
        builder.HasIndex(file => new { file.RegistryPackageVersionId, file.FileName }).IsUnique();
        builder.Property(file => file.FileName).HasMaxLength(255).IsRequired();
        builder.Property(file => file.FilePath).HasMaxLength(500).IsRequired();
        builder.Property(file => file.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(file => file.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(file => file.Sha1).HasMaxLength(40).IsRequired();
        builder.Property(file => file.Md5).HasMaxLength(32).IsRequired();
        builder.Property(file => file.Sha512).HasMaxLength(128).IsRequired();
        builder.Property(file => file.PublishedBy).HasMaxLength(100).IsRequired();
        builder.HasOne(file => file.RegistryPackageVersion)
            .WithMany(version => version.Files)
            .HasForeignKey(file => file.RegistryPackageVersionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
