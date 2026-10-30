// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>One notification to e-mail to one user; <see cref="To"/> is null when the account has no address.
/// <see cref="OrganizationId"/> is the organization the event's resource belongs to: its own version of the
/// template comes first; null for an event of no organization.</summary>
public sealed record NotificationEmail(string? To, string Subject, string EventType, string PayloadJson, int? OrganizationId = null);
