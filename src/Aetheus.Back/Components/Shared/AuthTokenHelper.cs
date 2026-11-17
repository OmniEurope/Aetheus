// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;

namespace Aetheus.Back.Components.Shared;

/// <summary>
/// Shared token primitives used by <see cref="AuthService"/> and <see cref="ServerEnrollmentService"/>.
/// Extracted to a single helper so neither service needs a <c>partial</c> split to share it. Token
/// hashing lives in <see cref="TokenHashKeys"/> (R-53).
/// </summary>
internal static class AuthTokenHelper
{
    public static string GenerateSecureToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string PadOrTrim(string input, int targetLength)
    {
        input ??= string.Empty;
        return input.Length == targetLength ? input : input.PadRight(targetLength, '\0');
    }
}
