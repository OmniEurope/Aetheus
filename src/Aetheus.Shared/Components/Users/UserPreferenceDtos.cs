// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Users;

/// <summary>
/// One preference of the signed-in user (<c>api/users/me/preferences</c>): the value is JSON written by
/// the browser, null for a removed preference (a tombstone kept so the removal reaches the user's other
/// devices), and the date of the change, the later of two values winning.
/// </summary>
public sealed record UserPreferenceDto
{
    public string Key { get; init; } = string.Empty;
    public string? Value { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A write of one preference: its JSON value (null removes it) and the date of the change.</summary>
public sealed record SaveUserPreferenceRequest
{
    [StringLength(UserPreferenceRules.MaxValueLength)]
    public string? Value { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
