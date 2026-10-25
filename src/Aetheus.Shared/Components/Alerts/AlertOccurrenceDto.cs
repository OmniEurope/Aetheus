// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Alerts;

/// <summary>
/// One alert that fired, as the alert page and the notification mail's button show it (recette R-52).
/// <see cref="Metric"/> is a <c>MetricType</c> name for a rule alert and the name of the host-security
/// check otherwise; the front words both.
/// </summary>
public sealed record AlertOccurrenceDto
{
    public long Id { get; init; }
    public int? AlertRuleId { get; init; }
    public string RuleName { get; init; } = string.Empty;

    /// <summary>Null when the occurrence is not a rule alert, or when its rule was deleted since.</summary>
    public bool? RuleIsEnabled { get; init; }

    public int? ServerId { get; init; }
    public string? ServerName { get; init; }
    public string Metric { get; init; } = string.Empty;
    public ComparisonOperator? Operator { get; init; }
    public double? Threshold { get; init; }
    public double? ObservedValue { get; init; }
    public int? SustainedSeconds { get; init; }
    public AlertSeverity Severity { get; init; }
    public string? Message { get; init; }
    public DateTime TriggeredAt { get; init; }
}

/// <summary>What a producer states when an alert fires: the service stamps it with an identifier.</summary>
public sealed record NewAlertOccurrence
{
    public int? AlertRuleId { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public int? ServerId { get; init; }
    public string? ServerName { get; init; }
    public string Metric { get; init; } = string.Empty;
    public ComparisonOperator? Operator { get; init; }
    public double? Threshold { get; init; }
    public double? ObservedValue { get; init; }
    public int? SustainedSeconds { get; init; }
    public AlertSeverity Severity { get; init; }
    public string? Message { get; init; }
    public DateTime TriggeredAt { get; init; }
}
