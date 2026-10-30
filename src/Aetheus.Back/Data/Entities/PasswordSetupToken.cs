// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// Recette R-14: the token of a set-password link (welcome mail, link sent again by an administrator, or
/// forgotten password), with which the person chooses their password once. Only its keyed hash is stored
/// (the same HMAC as the refresh and registration tokens); the plaintext exists in the mail alone. Spent
/// on first use, refused after <see cref="ExpiresAt"/>.
/// </summary>
public class PasswordSetupToken
{
    public int Id { get; set; }
    public int UserId { get; set; }

    /// <summary>HMAC-SHA256 of the plaintext token, base64. The plaintext is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Why the link was sent; it sets its lifetime and the per-address limits.</summary>
    public PasswordSetupPurpose Purpose { get; set; } = PasswordSetupPurpose.Welcome;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>When the token set the password, or was voided; null while usable.</summary>
    public DateTime? UsedAt { get; set; }

    public User User { get; set; } = null!;
}

/// <summary>Stored as its number: never renumber.</summary>
public enum PasswordSetupPurpose
{
    /// <summary>The welcome mail of an account an administrator created (7 days).</summary>
    Welcome = 1,

    /// <summary>A new link an administrator sent from the user's page (7 days).</summary>
    AdminLink = 2,

    /// <summary>The person asked for it from the sign-in page, "forgotten password" (1 hour).</summary>
    Reset = 3
}
