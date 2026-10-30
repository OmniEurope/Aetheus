// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Configuration;

/// <summary>
/// Strongly typed binding for the <c>Auth</c> configuration section.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string? JwtKey { get; set; }
    public string? JwtIssuer { get; set; }
    public string? JwtAudience { get; set; }
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 14;

    public string? AdminPassword { get; set; }

    public string? EncryptionKey { get; set; }
    public string? EncryptionSalt { get; set; }
}

/// <summary>
/// Strongly typed binding for the <c>Auth:AgentToken</c> sub-section.
/// </summary>
public sealed class AgentTokenOptions
{
    public const string SectionName = "Auth:AgentToken";

    public int RotationDays { get; set; } = 30;
    public int GracePeriodMinutes { get; set; } = 60;
}

/// <summary>
/// Strongly typed binding for the legacy <c>Auth</c> encryption sub-keys
/// (kept separate so callers that only need encryption don't have to depend on the whole Auth options).
/// </summary>
public sealed class EncryptionOptions
{
    public const string SectionName = "Auth";

    public string? EncryptionKey { get; set; }
    public string? EncryptionSalt { get; set; }
}
