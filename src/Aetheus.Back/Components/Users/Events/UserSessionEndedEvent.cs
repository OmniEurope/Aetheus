// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Users.Events;

/// <summary>Announces server-side session termination so higher modules can clear per-user
/// in-memory state without Auth or Users depending on them. Covers logout, replay revocation,
/// self-service password changes, deactivation and deletion.</summary>
public sealed record UserSessionEndedEvent(int UserId, string Reason) : IDomainEvent;

/// <summary>The <see cref="UserSessionEndedEvent.Reason"/> values, for the logs.</summary>
public static class UserSessionEndReasons
{
    public const string Logout = "logout";
    public const string RefreshReplay = "refresh_replay";
    public const string PasswordChanged = "password_changed";
    public const string Deactivated = "deactivated";
    public const string Deleted = "deleted";
}
