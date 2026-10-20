// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(e => e.Username).HasMaxLength(256);
        builder.Property(e => e.Email).HasMaxLength(320);
        builder.HasIndex(e => e.Username).IsUnique();
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.Property(e => e.Name).HasMaxLength(128);
        builder.Property(e => e.Description).HasMaxLength(512);
        builder.HasIndex(e => e.Name).IsUnique();
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        builder.HasKey(e => new { e.UserId, e.RoleId });
        builder.HasOne(e => e.User)
               .WithMany(u => u.UserRoles)
               .HasForeignKey(e => e.UserId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Role)
               .WithMany(r => r.UserRoles)
               .HasForeignKey(e => e.RoleId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ResourcePermissionConfiguration : IEntityTypeConfiguration<ResourcePermission>
{
    public void Configure(EntityTypeBuilder<ResourcePermission> builder)
    {
        builder.HasIndex(e => new { e.RoleId, e.ResourceType, e.ResourceId, e.Permission }).IsUnique();
        builder.HasOne(e => e.Role)
               .WithMany(r => r.ResourcePermissions)
               .HasForeignKey(e => e.RoleId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ExternalLoginConfiguration : IEntityTypeConfiguration<ExternalLogin>
{
    public void Configure(EntityTypeBuilder<ExternalLogin> builder)
    {
        builder.HasIndex(e => new { e.Provider, e.ProviderSubjectId }).IsUnique();
        builder.HasOne(e => e.User)
               .WithMany(u => u.ExternalLogins)
               .HasForeignKey(e => e.UserId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.Property(e => e.TokenHash).HasMaxLength(64);
        builder.HasIndex(e => e.TokenHash);
        builder.HasIndex(e => e.UserId);
        // Serves the cleanup sweep of revoked rows (RevokedAt < cutoff) without scanning live tokens.
        builder.HasIndex(e => e.RevokedAt).HasFilter("\"RevokedAt\" IS NOT NULL");
        builder.HasOne(e => e.User)
               .WithMany(u => u.RefreshTokens)
               .HasForeignKey(e => e.UserId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.ReplacedBy)
               .WithMany()
               .HasForeignKey(e => e.ReplacedById)
               .OnDelete(DeleteBehavior.SetNull);
    }
}
