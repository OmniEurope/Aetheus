// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Users.Events;

/// <summary>
/// Recette R1-37. Raised when an Aetheus user's session ends on the server's side: an explicit logout,
/// a refresh-token chain revoked as a replay, a self-service password change (the client signs out right
/// after), a deactivation or a deletion. Lets higher modules drop what they hold in memory for that user
/// (the typed mailbox passwords of the Mail module) without the emitters (Auth, L2; Users, L1) reaching
/// up to them. Declared here, in Users (L1), so both emitters and every listener sit above it.
/// </summary>
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
