// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class ServerConfiguration : IEntityTypeConfiguration<Server>
{
    public void Configure(EntityTypeBuilder<Server> builder)
    {
        builder.HasIndex(e => e.Hostname).IsUnique();
        builder.HasIndex(e => e.Name).IsUnique();
        builder.HasIndex(e => e.Status);
        builder.HasIndex(e => new { e.Status, e.LastHeartbeat });
        builder.Property(e => e.AgentSessionId).HasMaxLength(64);
        builder.Property(e => e.AgentCapabilitiesJson).HasMaxLength(8192);
        builder.Property(e => e.AgentSessionFencingToken).HasDefaultValue(0L);
        builder.HasIndex(e => e.OrganizationId);
        // Index machine fingerprints within organisations without enforcing uniqueness because
        // cloned images can share an identity.
        builder.Property(e => e.MachineIdHash).HasMaxLength(64);
        builder.HasIndex(e => new { e.OrganizationId, e.MachineIdHash });
        // A retired server keeps its row and links but disappears from every query that does not
        // opt out by name (see ServerQueryFilters). Hostname/Name stay globally unique on purpose: the
        // retired row still owns its identity, so the same machine revives it instead of duplicating it,
        // and the change stays expand-only (no index is dropped).
        builder.HasQueryFilter(ServerQueryFilters.ExcludeRetired, e => e.DeletedAt == null);
        builder.HasOne(e => e.Organization)
               .WithMany()
               .HasForeignKey(e => e.OrganizationId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RegistrationTokenConfiguration : IEntityTypeConfiguration<RegistrationToken>
{
    public void Configure(EntityTypeBuilder<RegistrationToken> builder)
    {
        // Stores a SHA-256/HMAC hash of the registration token (plaintext never persisted),
        // mirroring ServerToken.TokenHash / RefreshToken.TokenHash - fixed 64-char width.
        builder.Property(e => e.Token).HasMaxLength(64).IsRequired();
        builder.HasIndex(e => e.Token).IsUnique();
        builder.HasIndex(e => e.OrganizationId);
        builder.HasOne(e => e.UsedByServer)
               .WithMany()
               .HasForeignKey(e => e.UsedByServerId)
               .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(e => e.Organization)
               .WithMany()
               .HasForeignKey(e => e.OrganizationId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ServerTokenConfiguration : IEntityTypeConfiguration<ServerToken>
{
    public void Configure(EntityTypeBuilder<ServerToken> builder)
    {
        builder.Property(e => e.TokenHash).HasMaxLength(64);
        builder.HasIndex(e => e.TokenHash).IsUnique();
        builder.HasOne(e => e.Server)
               .WithMany(s => s.Tokens)
               .HasForeignKey(e => e.ServerId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
