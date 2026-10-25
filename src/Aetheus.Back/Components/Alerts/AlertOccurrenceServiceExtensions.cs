// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Alerts;

public static class AlertOccurrenceServiceExtensions
{
    /// <summary>Stores an alert raised about a host by one of the security checks (OOM kill, sudoers drift, SSH
    /// configuration, container risk): no rule, no threshold, a message. <paramref name="severity"/> is the
    /// producer's own word; one that is not a severity is stored as a warning.</summary>
    public static Task<long?> RecordHostAlertAsync(
        this IAlertOccurrenceService service,
        string ruleName,
        int serverId,
        string serverName,
        string metric,
        string severity,
        string? message,
        DateTime triggeredAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.RecordAsync(new NewAlertOccurrence
        {
            RuleName = ruleName,
            ServerId = serverId,
            ServerName = serverName,
            Metric = metric,
            Severity = Enum.TryParse<AlertSeverity>(severity, ignoreCase: true, out var parsed) ? parsed : AlertSeverity.Warning,
            Message = message,
            TriggeredAt = triggeredAt
        }, ct);
    }
}
