// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(e => e.Username).HasMaxLength(256);
        builder.Property(e => e.Email).HasMaxLength(320).IsRequired();
        builder.HasIndex(e => e.Username).IsUnique();
        // PLAN-003 P3-2: the e-mail is the sign-in identifier. Stored normalized (UserEmail.Normalize), so
        // this plain unique index is case-insensitive in effect. Every account has one (P3-4): NOT NULL, and a
        // blank one is refused as well, since an empty address signs nobody in.
        builder.HasIndex(e => e.Email).IsUnique();
        builder.ToTable(table => table.HasCheckConstraint("CK_Users_Email_NotBlank", "btrim(\"Email\") <> ''"));
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.Property(e => e.Name).HasMaxLength(128);
        builder.Property(e => e.Description).HasMaxLength(512);
        // Rows written without a date (seeds) and roles that existed before the columns take the database's clock (R-18).
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        // R-2: a role name is unique within its organization, and among the site roles (no organization, NULLS NOT
        // DISTINCT); two organizations name their own roles as they like. RoleRepository.RoleNameExistsAsync also
        // keeps an organization's role from taking a site role's name, so (organization, name) names one role.
        builder.HasIndex(e => new { e.OrganizationId, e.Name }).IsUnique().AreNullsDistinct(false);
        // An organization's own roles go with it.
        builder.HasOne(e => e.Organization).WithMany().HasForeignKey(e => e.OrganizationId).OnDelete(DeleteBehavior.Cascade);
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

internal sealed class UserOrganizationRoleConfiguration : IEntityTypeConfiguration<UserOrganizationRole>
{
    public void Configure(EntityTypeBuilder<UserOrganizationRole> builder)
    {
        builder.HasIndex(e => new { e.UserId, e.OrganizationId, e.RoleId }).IsUnique();
        builder.HasIndex(e => new { e.OrganizationId, e.RoleId });
        builder.HasOne(e => e.User)
               .WithMany(u => u.OrganizationRoles)
               .HasForeignKey(e => e.UserId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Organization)
               .WithMany()
               .HasForeignKey(e => e.OrganizationId)
               .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(e => e.Role)
               .WithMany(r => r.OrganizationAssignments)
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

internal sealed class RolePermissionGrantConfiguration : IEntityTypeConfiguration<RolePermissionGrant>
{
    public void Configure(EntityTypeBuilder<RolePermissionGrant> builder)
    {
        builder.Property(e => e.PermissionKey).HasMaxLength(80).IsRequired();
        // One grant per (role, key, resource): a null ResourceId (the whole organization) is one value too.
        builder.HasIndex(e => new { e.RoleId, e.PermissionKey, e.ResourceId }).IsUnique().AreNullsDistinct(false);
        builder.HasOne(e => e.Role)
               .WithMany(r => r.PermissionGrants)
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

internal sealed class PasswordSetupTokenConfiguration : IEntityTypeConfiguration<PasswordSetupToken>
{
    public void Configure(EntityTypeBuilder<PasswordSetupToken> builder)
    {
        builder.Property(e => e.TokenHash).HasMaxLength(64);
        builder.HasIndex(e => e.TokenHash).IsUnique();
        // Serves the per-account limits (links of one purpose issued within a window).
        builder.HasIndex(e => new { e.UserId, e.Purpose, e.CreatedAt });
        builder.HasOne(e => e.User)
               .WithMany()
               .HasForeignKey(e => e.UserId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
