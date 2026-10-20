// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Shared.Components.Users;

/// <summary>
/// The bounds of the per-user preferences (recette R1-66), shared by the API, which enforces them, and
/// the browser, which checks them before sending so a refused write is not retried at every
/// synchronization.
/// </summary>
public static partial class UserPreferenceRules
{
    /// <summary>The longest key: OE grid keys are <c>omni.grid.</c> followed by the grid's state key.</summary>
    public const int MaxKeyLength = 200;

    /// <summary>The longest JSON value of one preference, in characters.</summary>
    public const int MaxValueLength = 32 * 1024;

    /// <summary>The sum of the value lengths one user may keep, in characters.</summary>
    public const int MaxTotalLength = 512 * 1024;

    /// <summary>The number of keys one user may keep, removed ones (tombstones) included.</summary>
    public const int MaxKeys = 500;

    /// <summary>Letters, digits, dot, dash, underscore and colon, starting with a letter or a digit.</summary>
    public static bool IsValidKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength && KeyPattern().IsMatch(key);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
