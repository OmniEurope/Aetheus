// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Users.Events;

/// <summary>
/// Recette R-14: an administrator created an account, whose owner gets a welcome mail with a link to
/// choose their password. Auth sends it (it owns the token and sits above Users and Notifications) and
/// writes what happened into <see cref="Outcome"/>; the creation answers it to the administrator. Left
/// null (no handler, or a handler that threw), the creation says the mail did not leave.
/// </summary>
public sealed class UserWelcomeRequestedEvent(int userId, string username, string email) : IDomainEvent
{
    public int UserId { get; } = userId;
    public string Username { get; } = username;
    public string Email { get; } = email;
    public WelcomeMailResultDto? Outcome { get; set; }
}
