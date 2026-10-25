// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// One alert that fired, kept so it has an identifier of its own and an address in the app (recette
/// R-52). The rule and the server are snapshotted by name: the occurrence outlives a deleted rule or a
/// retired server, which only clear the link.
/// </summary>
public class AlertOccurrence
{
    public long Id { get; set; }

    /// <summary>The metric rule that fired, or null for a host-security alert (OOM kill, sudoers drift...) and once the rule is deleted.</summary>
    public int? AlertRuleId { get; set; }

    public string RuleName { get; set; } = string.Empty;
    public int? ServerId { get; set; }
    public string? ServerName { get; set; }

    /// <summary>The <see cref="MetricType"/> name of a rule alert, or the name of the host-security check that raised it.</summary>
    public string Metric { get; set; } = string.Empty;

    public ComparisonOperator? Operator { get; set; }
    public double? Threshold { get; set; }

    /// <summary>The latest value measured when the rule fired, in the unit of the metric; null when the alert is not a measure.</summary>
    public double? ObservedValue { get; set; }

    public int? SustainedSeconds { get; set; }
    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;
    public string? Message { get; set; }
    public DateTime TriggeredAt { get; set; }

    public AlertRule? AlertRule { get; set; }
    public Server? Server { get; set; }
}
