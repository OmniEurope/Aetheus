// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Shared;

/// <summary>
/// Strongly-typed payload broadcast on the <c>AlertTriggered</c> SignalR event,
/// replacing the previous anonymous objects scattered across alert sources.
/// </summary>
public sealed record AlertTriggeredDto
{
    /// <summary>The identifier of the stored occurrence, hence of the alert's page; null for an alert source that does not store its occurrences yet.</summary>
    public long? OccurrenceId { get; init; }
    public int? RuleId { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public int? ServerId { get; init; }
    public string? ServerName { get; init; }
    public string Metric { get; init; } = string.Empty;
    public string? Operator { get; init; }
    public double? Threshold { get; init; }
    public string Severity { get; init; } = string.Empty;
    public string? Message { get; init; }
    public DateTime TriggeredAt { get; init; }
}
