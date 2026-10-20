// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Servers.Events;

/// <summary>A container was first seen with host-escape settings its Aetheus role does not explain, or
/// those settings changed (<paramref name="IsChange"/>). An unchanged state is not raised again.</summary>
public sealed record DockerContainerRiskDetectedEvent(
    int ServerId,
    string ServerName,
    string ContainerName,
    string Image,
    string Risks,
    string Severity,
    string Message,
    bool IsChange,
    DateTime DetectedAt) : IDomainEvent;
