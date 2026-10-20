// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;

namespace Aetheus.Shared.Components.Vaults;

/// <summary>
/// Generates cryptographically secure values for the well-known secret keys the deployment
/// scripts expect. The recipes mirror <c>deploy/scripts/deploy.sh</c> and
/// <c>deploy/scripts/prod-deploy-prepare.sh</c> byte for byte, so a value generated here is
/// interchangeable with one a deployment generated on the host. An unknown key falls back to
/// the generic profile rather than refusing: a strong random value is always better than a
/// hand-typed one.
/// </summary>
public static class VaultSecretGenerator
{
    /// <summary>How a generated value is encoded once random bytes are drawn.</summary>
    public enum Encoding
    {
        /// <summary>Standard base64, kept verbatim (padding and all).</summary>
        Base64,

        /// <summary>Base64 with <c>/</c>, <c>+</c> and <c>=</c> removed, matching <c>tr -d "/+="</c>.</summary>
        Base64UrlSafeStripped,

        /// <summary>Lowercase hexadecimal, matching <c>openssl rand -hex</c>.</summary>
        Hex
    }

    /// <summary>The recipe for one well-known key: how many random bytes, and how to encode them.</summary>
    public readonly record struct Profile(string Key, int RandomBytes, Encoding Encoding, string Description);

    /// <summary>Applied to any key with no dedicated recipe. 48 bytes is the strongest recipe in use.</summary>
    public static readonly Profile Generic =
        new(string.Empty, 48, Encoding.Base64UrlSafeStripped, "Generic strong secret");

    private static readonly Profile[] KnownProfiles =
    [
        // Generates the 24-byte hexadecimal secret used by #{SECRET_HEX_24}# in
        // deploy/env/prod.env.sample.
        new("DB_PASSWORD", 24, Encoding.Hex, "PostgreSQL password"),
        // head -c 48 /dev/urandom | base64 | tr -d "/+=" (deploy.sh)
        new("JWT_KEY", 48, Encoding.Base64UrlSafeStripped, "JWT signing key"),
        new("ENCRYPTION_KEY", 48, Encoding.Base64UrlSafeStripped, "Data encryption key"),
        // head -c 24 /dev/urandom | base64 (deploy.sh) - padding is part of the value here.
        new("ENCRYPTION_SALT", 24, Encoding.Base64, "Data encryption salt")
    ];

    /// <summary>The keys that have a dedicated recipe, in declaration order.</summary>
    public static IReadOnlyList<Profile> Profiles => KnownProfiles;

    /// <summary>
    /// Returns the recipe for <paramref name="key"/>, or <see cref="Generic"/> when the key is not
    /// one of the well-known ones. Matching is case-insensitive and ignores surrounding whitespace.
    /// </summary>
    public static Profile ProfileFor(string? key)
    {
        var trimmed = key?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return Generic;

        foreach (var profile in KnownProfiles)
            if (string.Equals(profile.Key, trimmed, StringComparison.OrdinalIgnoreCase)) return profile;

        return Generic;
    }

    /// <summary>True when <paramref name="key"/> has a dedicated recipe rather than the generic one.</summary>
    public static bool IsKnownKey(string? key) => ProfileFor(key).Key.Length > 0;

    /// <summary>
    /// Generates a value for <paramref name="key"/> using its recipe. Randomness comes from
    /// <see cref="RandomNumberGenerator"/>, never from <see cref="Random"/>.
    /// </summary>
    public static string Generate(string? key) => Generate(ProfileFor(key));

    /// <summary>Generates a value from an explicit recipe.</summary>
    public static string Generate(Profile profile)
    {
        var bytes = RandomNumberGenerator.GetBytes(profile.RandomBytes);
        try
        {
            return profile.Encoding switch
            {
                Encoding.Hex => Convert.ToHexStringLower(bytes),
                Encoding.Base64 => Convert.ToBase64String(bytes),
                Encoding.Base64UrlSafeStripped => Strip(Convert.ToBase64String(bytes)),
                _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Encoding, "Unsupported encoding.")
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>The letters and digits of <see cref="GenerateCharacters"/>: safe in any shell, URL or file.</summary>
    private const string Alphanumeric = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary> A value of exactly <paramref name="length"/> letters and digits, drawn uniformly
    /// by <see cref="RandomNumberGenerator"/>, for a secret whose consumer wants a set length
    /// rather than one of the key recipes. </summary>
    public static string GenerateCharacters(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        return RandomNumberGenerator.GetString(Alphanumeric, length);
    }

    /// <summary>Mirrors <c>tr -d "/+="</c>: drops the three characters, keeps everything else.</summary>
    private static string Strip(string base64)
    {
        Span<char> buffer = base64.Length <= 128 ? stackalloc char[base64.Length] : new char[base64.Length];
        var written = 0;
        foreach (var character in base64)
            if (character is not ('/' or '+' or '=')) buffer[written++] = character;
        return new string(buffer[..written]);
    }
}
