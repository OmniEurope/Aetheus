// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Auth;

/// <summary>
/// A person proved they hold an account: a sign-in that passed the password and the second factor, or a password
/// set from a link mailed to the account's address. The administrator bootstrap reads it (owner decision
/// 2026-10-28) and says in <see cref="GrantedSiteAdmin"/> whether the account was just made site Admin, so the
/// sign-in issues a token that already carries the role.
/// </summary>
public sealed class AccountAccessProvedEvent(int userId, string username, string? email) : IDomainEvent
{
    public int UserId { get; } = userId;
    public string Username { get; } = username;
    public string? Email { get; } = email;
    public bool GrantedSiteAdmin { get; set; }
}
