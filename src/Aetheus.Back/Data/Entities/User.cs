// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    /// <summary>The sign-in identifier (PLAN-003): required, stored normalized (<c>UserEmail.Normalize</c>) and
    /// unique. The database refuses a missing or blank one (NOT NULL and <c>CK_Users_Email_NotBlank</c>).</summary>
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Last time the account authenticated (a login or a token refresh); null until the first one
    /// after this column was added. Not bumped by an administrator editing the account (that is UpdatedAt).</summary>
    public DateTime? LastActivityAt { get; set; }

    /// <summary>
    /// When true, the user is forced onto a mandatory password-change screen at login and cannot
    /// reach the rest of the app until they set a new password. Set on admin-provisioned accounts;
    /// cleared the first time the user changes their own password.
    /// </summary>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// R-1 (owner decision 2026-10-29): set on the administrator a fresh install seeds with its default address
    /// and the configured password. Until its holder chooses their own name, address and password
    /// (<c>POST /api/users/me/account-setup</c>, the only call that clears it) the account reaches nothing else:
    /// its tokens carry the forced-change claim the middleware enforces, and the plain password change is refused.
    /// </summary>
    public bool MustSetUpAccount { get; set; }

    /// <summary>
    /// Bumped on password change, role change, or explicit logout. Embedded as a JWT claim so
    /// stale tokens can be rejected during validation (immediate revocation).
    /// </summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    // --- Lockout (F-011) ---
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }

    // --- TOTP 2FA (F-010) ---
    /// <summary>Base32-encoded TOTP secret, encrypted at rest via EncryptionService.</summary>
    public string? TotpSecret { get; set; }
    public bool TotpEnabled { get; set; }
    /// <summary>JSON array of BCrypt-hashed recovery codes. Null when TOTP is not set up.</summary>
    public string? TotpRecoveryCodes { get; set; }

    // Navigation
    public List<UserRole> UserRoles { get; set; } = [];
    public List<UserOrganizationRole> OrganizationRoles { get; set; } = [];
    public List<ExternalLogin> ExternalLogins { get; set; } = [];
    public List<RefreshToken> RefreshTokens { get; set; } = [];
}
