// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.ServerApps.Events;

/// <summary>Announces application port changes so an upper-layer handler can maintain the registry
/// without coupling same-layer storage modules. A null <see cref="ServerId"/> or <see cref="Port"/>
/// releases the application’s claim (ADR-039).</summary>
public sealed record ServerAppPortChangedEvent(
    int ServerAppId,
    int? ServerId,
    int? Port,
    string AppName) : IDomainEvent;
