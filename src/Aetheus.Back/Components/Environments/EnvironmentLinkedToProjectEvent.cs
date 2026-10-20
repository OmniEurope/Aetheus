// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Environments;

/// <summary>Announces a project-linked environment so Pipelines can copy its definitions without an
/// upward Environments dependency (ADR-039).</summary>
public sealed record EnvironmentLinkedToProjectEvent(int EnvironmentId, string EnvironmentName, int ProjectId) : IDomainEvent;
