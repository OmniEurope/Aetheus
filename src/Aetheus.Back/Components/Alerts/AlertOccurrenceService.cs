// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Alerts;

public sealed class AlertOccurrenceService(
    IAlertOccurrenceRepository repo,
    ILogger<AlertOccurrenceService> logger) : IAlertOccurrenceService
{
    private const int RuleNameLength = 200;
    private const int ServerNameLength = 200;
    private const int MetricLength = 100;
    private const int MessageLength = 4000;

    public async Task<long?> RecordAsync(NewAlertOccurrence occurrence, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var entity = new AlertOccurrence
        {
            AlertRuleId = occurrence.AlertRuleId,
            RuleName = Truncate(occurrence.RuleName, RuleNameLength),
            ServerId = occurrence.ServerId,
            ServerName = occurrence.ServerName is null ? null : Truncate(occurrence.ServerName, ServerNameLength),
            Metric = Truncate(occurrence.Metric, MetricLength),
            Operator = occurrence.Operator,
            Threshold = occurrence.Threshold,
            ObservedValue = occurrence.ObservedValue,
            SustainedSeconds = occurrence.SustainedSeconds,
            Severity = occurrence.Severity,
            Message = occurrence.Message is null ? null : Truncate(occurrence.Message, MessageLength),
            TriggeredAt = occurrence.TriggeredAt
        };
        try
        {
            await repo.AddAsync(entity, ct).ConfigureAwait(false);
            return entity.Id;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is DbUpdateException or System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            // Mandatory catch: the occurrence only gives the alert its page; the mail, the webhook and the toast
            // must still go out when the database refuses the row.
            logger.LogWarning(ex,
                "Alert '{RuleName}' on server {ServerId} could not be stored; it is emitted without its page",
                entity.RuleName, entity.ServerId);
            return null;
        }
    }

    public async Task<AlertOccurrenceDto?> GetAsync(long id, CancellationToken ct = default)
    {
        var occurrence = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);
        return occurrence is null ? null : new AlertOccurrenceDto
        {
            Id = occurrence.Id,
            AlertRuleId = occurrence.AlertRuleId,
            RuleName = occurrence.RuleName,
            RuleIsEnabled = occurrence.AlertRule?.IsEnabled,
            ServerId = occurrence.ServerId,
            ServerName = occurrence.ServerName,
            Metric = occurrence.Metric,
            Operator = occurrence.Operator,
            Threshold = occurrence.Threshold,
            ObservedValue = occurrence.ObservedValue,
            SustainedSeconds = occurrence.SustainedSeconds,
            Severity = occurrence.Severity,
            Message = occurrence.Message,
            TriggeredAt = occurrence.TriggeredAt
        };
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
