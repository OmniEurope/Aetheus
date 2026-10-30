// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Auth;

/// <summary>
/// Recette R-14: the person who received a welcome mail chooses their password with the single-use
/// token of its link. Anonymous: the token is the only proof.
/// </summary>
public sealed record CompletePasswordSetupRequest
{
    [Required]
    [StringLength(100)]
    public string Token { get; init; } = string.Empty;

    [Required]
    [StringLength(PasswordPolicy.MaximumLength, MinimumLength = PasswordPolicy.MinimumLength)]
    public string NewPassword { get; init; } = string.Empty;
}

/// <summary>"Forgotten password" (R-14): the address the person signs in with. The answer is the same whether
/// an active account holds it or not.</summary>
public sealed record RequestPasswordResetRequest
{
    [Required]
    [StringLength(200)]
    [EmailAddress]
    public string Email { get; init; } = string.Empty;
}

/// <summary>Where the welcome mail's link leads. The token travels in the fragment (<c>#token=</c>), which the
/// browser never sends to a server: no access log or Referer header carries it.</summary>
public static class PasswordSetupLink
{
    public const string PagePath = "/login/set-password";
    public const string TokenFragmentKey = "token";

    public static string Path(string token) => $"{PagePath}#{TokenFragmentKey}={Uri.EscapeDataString(token)}";
}

/// <summary>The <see cref="ApiError.Code"/> of a refused password setup.</summary>
public static class PasswordSetupErrorCodes
{
    /// <summary>Unknown, already used or expired link, or an account no longer active: one answer for all.</summary>
    public const string InvalidOrExpired = "PasswordSetupInvalidOrExpired";
}
