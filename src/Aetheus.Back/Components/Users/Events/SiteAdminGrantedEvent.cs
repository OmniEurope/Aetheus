// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Users.Events;

/// <summary>
/// Owner decision 2026-10-28: an account newly holds the site Admin role (it did not before this change). Auth
/// mails the person (it owns the mail of the accounts) and audits whether the mail left; the grant stands either way.
/// </summary>
public sealed record SiteAdminGrantedEvent(int UserId, string Username, string? Email) : IDomainEvent;
