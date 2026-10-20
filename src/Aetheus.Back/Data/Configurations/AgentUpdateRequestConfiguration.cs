// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aetheus.Back.Data.Configurations;

internal sealed class AgentUpdateRequestConfiguration : IEntityTypeConfiguration<AgentUpdateRequest>
{
    public void Configure(EntityTypeBuilder<AgentUpdateRequest> builder)
    {
        builder.Property(request => request.TargetVersion).HasMaxLength(50);
        builder.Property(request => request.ObservedVersion).HasMaxLength(50);
        builder.Property(request => request.ObservedSessionId).HasMaxLength(64);
        builder.Property(request => request.ConfirmedSessionId).HasMaxLength(64);
        builder.Property(request => request.RequestedBy).HasMaxLength(256);
        builder.Property(request => request.ExpectedCapabilitiesJson).HasMaxLength(8192);
        builder.Property(request => request.EnableCapabilities).HasMaxLength(128);
        builder.Property(request => request.FailureCode).HasMaxLength(64);
        builder.Property(request => request.FailureDiagnostic).HasMaxLength(1000);
        builder.HasIndex(request => new { request.ServerId, request.TargetVersion })
            .IsUnique()
            .HasFilter("\"IsActive\" = TRUE");
        builder.HasIndex(request => new { request.IsActive, request.Status });
        builder.HasOne(request => request.Server)
            .WithMany(server => server.AgentUpdateRequests)
            .HasForeignKey(request => request.ServerId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(request => request.Task)
            .WithMany()
            .HasForeignKey(request => request.TaskId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
