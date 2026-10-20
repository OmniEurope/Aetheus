// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Aetheus.WebAnalytics;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnalyticsBrowserEvent
{
    [Range(1, 1)]
    public int SchemaVersion { get; init; }

    [Required]
    public Guid EventId { get; init; }

    [Required]
    public DateTimeOffset OccurredAtUtc { get; init; }

    [Required]
    [StringLength(32, MinimumLength = 1)]
    public string Kind { get; init; } = string.Empty;

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Route { get; init; } = string.Empty;

    [Range(0, 300_000)]
    public int? DurationMs { get; init; }

    [StringLength(32, MinimumLength = 1)]
    [RegularExpression("^[a-z0-9_]+$")]
    public string? ErrorType { get; init; }

    /// <summary>
    /// The host app says the visitor is signed in, and nothing more: no account, no token. The static
    /// front server cannot authenticate the visitor itself (the app talks to the API with a bearer
    /// token), so this yes/no is the only way a signed-in visit is counted. It is a declaration, not a
    /// proof, which is acceptable for a count and never used for anything else.
    /// </summary>
    public bool? SignedIn { get; init; }

    /// <summary>Optional opaque visitor identifier used only for pseudonym derivation when the host
    /// opts in and lacks its own authenticated identity. It is neither stored nor proof of
    /// authentication and must not contain an account name or email address.</summary>
    [StringLength(256, MinimumLength = 1)]
    [RegularExpression("^[A-Za-z0-9_-]+$")]
    public string? AuthenticatedUserId { get; init; }
}

internal sealed record AnalyticsExportEvent
{
    public int SchemaVersion { get; init; } = 1;
    public int ApplicationId { get; init; }
    public string SiteId { get; init; } = string.Empty;
    public Guid EventId { get; init; }
    public DateTime OccurredAtUtc { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Route { get; init; } = string.Empty;
    public int? DurationMs { get; init; }
    public string? ErrorType { get; init; }
    public string DailyPseudonym { get; init; } = string.Empty;
    public string WeeklyPseudonym { get; init; } = string.Empty;
    public string MonthlyPseudonym { get; init; } = string.Empty;
    public string SessionPseudonym { get; init; } = string.Empty;
    public string? AuthenticatedPseudonym { get; init; }
    public int KeyVersion { get; init; }
}
