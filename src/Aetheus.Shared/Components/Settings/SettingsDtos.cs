// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Aetheus.Shared.Components.Settings;

public sealed record RegistrationTokenDto
{
    public int Id { get; init; }

    [SuppressMessage("Aetheus.Security", "SEC004",
        Justification = "The registration token IS the artefact this contract exists to hand to an operator so a new agent can enrol; it is single-use and its consumption is recorded on IsUsed.")]
    public string Token { get; init; } = string.Empty;
    public bool IsUsed { get; init; }
    public int? UsedByServerId { get; init; }
    public int OrganizationId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}

public sealed record CreateRegistrationTokenRequest
{
    [Range(1, 8760)]
    public int ExpirationHours { get; init; } = 24;

    /// <summary>Organization the registered server will be attached to. Optional -
    /// when omitted, the API falls back to the caller's default organization.</summary>
    [Range(1, int.MaxValue)]
    public int? OrganizationId { get; init; }

}

public sealed record AppSettingDto
{
    [Required]
    [StringLength(200)]
    public string Key { get; init; } = string.Empty;

    [Required]
    [StringLength(10000)]
    public string Value { get; init; } = string.Empty;
}

public sealed record SecretDto
{
    public int Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record CreateSecretRequest
{
    [Required]
    [StringLength(100)]
    public string Key { get; init; } = string.Empty;

    [Required]
    [StringLength(10000)]
    public string Value { get; init; } = string.Empty;
}

/// <summary>A sign-in. Everyone signs in with their e-mail address, and only with it (owner decision
/// 2026-10-28): the account's username is a display name, never a credential. A request without an e-mail is
/// malformed; an e-mail field holding something that is no account's address is refused like a wrong
/// password.</summary>
public sealed record LoginRequest
{
    [Required]
    [StringLength(320)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string Password { get; init; } = string.Empty;

    public bool RememberMe { get; init; }

    /// <summary>TOTP code, required when the account has 2FA enabled.</summary>
    [StringLength(10)]
    public string? TotpCode { get; init; }

    /// <summary>Recovery code, used instead of TotpCode when the user has lost their authenticator.</summary>
    [StringLength(20)]
    public string? RecoveryCode { get; init; }
}

public sealed record LoginResponse
{
    [SuppressMessage("Aetheus.Security", "SEC004",
        Justification = "Authentication response. The access token is the answer to a successful login and is returned to the caller that just proved its identity; there is no form of this contract that omits it.")]
    public string Token { get; init; } = string.Empty;
    public DateTime ExpiresAt { get; init; }

    /// <summary>Opaque refresh token for obtaining new access tokens without re-authenticating.</summary>
    [SuppressMessage("Aetheus.Security", "SEC004",
        Justification = "Authentication response, same reason as Token above: issued to the caller that just authenticated, and stored server-side only as a hash.")]
    public string? RefreshToken { get; init; }

    /// <summary>When true, the caller must re-submit with a TotpCode or RecoveryCode.</summary>
    public bool TotpRequired { get; init; }

    /// <summary>
    /// When true, the authenticated user must change their password before using the app. The
    /// Front routes them to a mandatory change-password screen. The token is still issued so the
    /// change-password call can authenticate.
    /// </summary>
    public bool MustChangePassword { get; init; }
}

public sealed record PublicDemoInfoDto
{
    public bool Enabled { get; init; }
}

public sealed record RefreshTokenRequest
{
    [Required]
    [StringLength(200)]
    public string RefreshToken { get; init; } = string.Empty;
}

/// <summary>Reports an unsolicited client session end anonymously, using only an allowed reason
/// code and correlation identifier.</summary>
public sealed record SessionEndedReport
{
    /// <summary>One of <c>SessionEndReasons.All</c>; anything else is refused.</summary>
    [Required]
    [StringLength(40)]
    public string Reason { get; init; } = string.Empty;

    [Required]
    [StringLength(64)]
    [RegularExpression("^[A-Za-z0-9-]+$")]
    public string CorrelationId { get; init; } = string.Empty;
}

// --- TOTP 2FA DTOs (F-010) ---

public sealed record TotpSetupResponse
{
    /// <summary>Base32-encoded shared secret for the authenticator app.</summary>
    public string SharedKey { get; init; } = string.Empty;

    /// <summary>otpauth:// URI for QR code generation.</summary>
    public string AuthenticatorUri { get; init; } = string.Empty;

    /// <summary>One-time recovery codes (plaintext, shown once).</summary>
    public List<string> RecoveryCodes { get; init; } = [];
}

public sealed record TotpVerifyRequest
{
    [Required]
    [StringLength(10)]
    public string Code { get; init; } = string.Empty;
}

public sealed record TotpDisableRequest
{
    [Required]
    [StringLength(200)]
    public string Password { get; init; } = string.Empty;
}

public sealed record ExternalLoginRequest
{
    [Required]
    [StringLength(50)]
    public string Provider { get; init; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string SubjectId { get; init; } = string.Empty;

    [StringLength(200)]
    public string? DisplayName { get; init; }

    [StringLength(200)]
    [EmailAddress]
    public string? Email { get; init; }
}

public sealed record DashboardOverviewDto
{
    public int TotalServers { get; init; }
    public int OnlineServers { get; init; }
    public int OfflineServers { get; init; }
    public int PendingTasks { get; init; }
    public int RunningPipelines { get; init; }

    /// <summary>Every project the caller can read; <see cref="Projects"/> only lists the recent ones.</summary>
    public int TotalProjects { get; init; }
    public List<PipelineRunDto> RecentRuns { get; init; } = [];
    public List<ServerDto> Servers { get; init; } = [];
    public List<ProjectDto> Projects { get; init; } = [];
}
