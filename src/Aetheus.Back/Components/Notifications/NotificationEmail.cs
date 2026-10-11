// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Notifications;

/// <summary>One notification to e-mail to one user; <see cref="To"/> is null when the account has no address.</summary>
public sealed record NotificationEmail(string? To, string Subject, string EventType, string PayloadJson);
