// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Servers.Events;

/// <summary>Reports sudoers drift when first detected and every <see
/// cref="SudoersDriftMonitor.ReminderInterval"/> while it persists, with <paramref
/// name="IsReminder"/> identifying repeat alerts.</summary>
public sealed record SudoersDriftDetectedEvent(
    int ServerId,
    string ServerName,
    string Files,
    string Message,
    bool IsReminder,
    DateTime DetectedAt) : IDomainEvent;
