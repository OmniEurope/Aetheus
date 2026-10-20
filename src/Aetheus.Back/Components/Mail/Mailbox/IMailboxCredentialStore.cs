// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// A held mailbox login. <see cref="Login"/> is null for a password the reader typed (the login is then the
/// mailbox address); it is the master login (recette R1-41) when an administrator opened the mailbox
/// without its password. Never logged: <see cref="ToString"/> leaves the password out.
/// </summary>
public sealed record MailboxHeldCredential(string Password, string? Login)
{
    public bool IsAdministratorAccess => Login is not null;

    public override string ToString() => $"MailboxHeldCredential {{ Login = {Login} }}";
}

/// <summary>
/// Recette R1-17. The mailbox passwords readers typed, held in the backend's memory only: never written
/// to the database, a log or the audit trail, lost on restart, and forgotten after
/// <see cref="MailboxCredentialStore.IdleTimeout"/> without use or <see cref="MailboxCredentialStore.MaxLifetime"/>
/// in all, whichever comes first. Each entry belongs to one Aetheus user: another user, even with the
/// same rights on the server, has to type the password again.
/// </summary>
public interface IMailboxCredentialStore
{
    /// <summary>
    /// Holds the password and returns when it will be forgotten unless used (UTC). <paramref name="login"/>
    /// is the login it opens the mailbox with, null for the mailbox address (recette R1-41).
    /// </summary>
    DateTime Store(MailboxCredentialKey key, string password, string? login = null);

    /// <summary>The password, or null when it was never given, was locked or expired. A hit extends the idle window.</summary>
    string? TryUse(MailboxCredentialKey key);

    /// <summary>The held login and password, or null; a hit extends the idle window like <see cref="TryUse"/>.</summary>
    MailboxHeldCredential? TryUseCredential(MailboxCredentialKey key);

    /// <summary>When the held password will be forgotten unless used (UTC), or null when none is held. Does not extend it.</summary>
    DateTime? ExpiresAt(MailboxCredentialKey key);

    /// <summary>Recette R1-41: true while the entry held for the key is an administrator access. Does not extend it.</summary>
    bool IsAdministratorAccess(MailboxCredentialKey key);

    /// <summary>Forgets the password ("lock").</summary>
    void Forget(MailboxCredentialKey key);

    /// <summary> Forgets every password the Aetheus user <paramref name="userId"/> holds, on every
    /// server and account, when their session ends. Other users' passwords stay. Returns how many
    /// were forgotten. </summary>
    int ForgetUser(int userId);
}
