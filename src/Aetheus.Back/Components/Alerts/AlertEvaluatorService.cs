// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Alerts;

public sealed class AlertEvaluatorService(
    IServiceScopeFactory scopeFactory,
    IHubContext<AlertHub> alertHub,
    ILogger<AlertEvaluatorService> logger,
    TimeProvider timeProvider,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    private static readonly TimeSpan MaximumMetricGap = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    // Only the live colour runs it (decision of 2026-10-02, PostgresLeaderLease).
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync("aetheus:alert-evaluator", RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        // do..while: evaluate immediately on startup, then every interval
        do
        {
            try
            {
                await EvaluateAlertRulesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in AlertEvaluatorService");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task EvaluateAlertRulesAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var alertRepo = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

        var rules = await alertRepo.GetEnabledAsync(ct).ConfigureAwait(false);

        var serverGroups = rules.Where(rule => rule.ServerId is not null)
            .GroupBy(rule => rule.ServerId!.Value)
            .ToList();
        var maximumWindowSeconds = serverGroups.Count == 0
            ? 0
            : checked(serverGroups.Max(group => group.Max(rule => rule.SustainedSeconds))
                + (int)MaximumMetricGap.TotalSeconds);
        var metricsByServer = await alertRepo.GetRecentMetricsForServersAsync(
            serverGroups.Select(group => group.Key).ToArray(),
            maximumWindowSeconds,
            ct).ConfigureAwait(false);

        foreach (var serverGroup in serverGroups)
        {
            if (!metricsByServer.TryGetValue(serverGroup.Key, out var serverMetrics)
                || serverMetrics.Count == 0)
                continue;

            foreach (var rule in serverGroup)
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                var since = now.AddSeconds(-rule.SustainedSeconds);
                var boundary = serverMetrics
                    .Where(metric => metric.Timestamp <= since)
                    .MaxBy(metric => metric.Timestamp);
                if (boundary is null) continue;

                var metrics = serverMetrics
                    .Where(metric => metric.Timestamp >= boundary.Timestamp && metric.Timestamp <= now)
                    .OrderBy(metric => metric.Timestamp)
                    .ToList();
                if (!HasContinuousCoverage(metrics, since, now)) continue;

                var allBreached = metrics.All(m => IsThresholdBreached(rule, m));
                if (!allBreached) continue;

                // Only trigger if enough time has passed since last trigger (avoid spam)
                if (rule.LastTriggeredAt.HasValue &&
                    (timeProvider.GetUtcNow().UtcDateTime - rule.LastTriggeredAt.Value).TotalSeconds < rule.SustainedSeconds * 2)
                    continue;

                // BACK-004: the cooldown is reserved and committed before anything is emitted, so a
                // second evaluator that read the same cooldown cannot emit the same alert again.
                var triggeredAt = timeProvider.GetUtcNow().UtcDateTime;
                if (!await alertRepo.TryReserveTriggerAsync(rule.Id, rule.LastTriggeredAt, triggeredAt, ct).ConfigureAwait(false))
                    continue;
                rule.LastTriggeredAt = triggeredAt;

                logger.LogWarning(
                    "Alert '{RuleName}' triggered: server {ServerId} {Metric} {Operator} {Threshold} for {Seconds}s",
                    rule.Name, rule.ServerId, rule.Metric, rule.Operator, rule.Threshold, rule.SustainedSeconds);

                await notificationService.SendEventAsync("alert.triggered", new
                {
                    RuleName = rule.Name,
                    rule.ServerId,
                    Metric = rule.Metric.ToString(),
                    Operator = rule.Operator.ToString(),
                    rule.Threshold,
                    rule.SustainedSeconds
                }, ct).ConfigureAwait(false);

                await alertHub.Clients.Group(HubGroups.Alerts).SendAsync("AlertTriggered", new AlertTriggeredDto
                {
                    RuleId = rule.Id,
                    RuleName = rule.Name,
                    ServerId = rule.ServerId,
                    ServerName = rule.Server?.Name,
                    Metric = rule.Metric.ToString(),
                    Operator = rule.Operator.ToString(),
                    Threshold = rule.Threshold,
                    Severity = rule.Severity.ToString(),
                    TriggeredAt = rule.LastTriggeredAt ?? timeProvider.GetUtcNow().UtcDateTime
                }, ct).ConfigureAwait(false);
            }
        }
    }

    internal static bool HasContinuousCoverage(
        IReadOnlyList<ServerMetric> metrics,
        DateTime since,
        DateTime now)
    {
        if (metrics.Count == 0 || metrics[0].Timestamp > since || now - metrics[^1].Timestamp > MaximumMetricGap)
            return false;

        for (var index = 1; index < metrics.Count; index++)
        {
            if (metrics[index].Timestamp - metrics[index - 1].Timestamp > MaximumMetricGap)
                return false;
        }
        return true;
    }

    internal static bool IsThresholdBreached(AlertRule rule, ServerMetric metric)
    {
        var metricValue = ReadMetricValue(rule.Metric, metric);
        return metricValue is not null && Compare(metricValue.Value, rule.Operator, rule.Threshold);
    }

    private static double? ReadMetricValue(MetricType metricType, ServerMetric metric) =>
        metricType switch
        {
            MetricType.Cpu => metric.CpuPercent,
            MetricType.Memory => metric.MemoryTotalMb > 0
                ? (metric.MemoryUsedMb / metric.MemoryTotalMb) * 100
                : null,
            MetricType.Disk => metric.DiskTotalGb > 0
                ? (metric.DiskUsedGb / metric.DiskTotalGb) * 100
                : null,
            MetricType.DiskFree => metric.DiskTotalGb > 0
                ? Math.Max(0, metric.DiskTotalGb - metric.DiskUsedGb)
                : null,
            MetricType.BuildCache => metric.BuildCacheAvailable
                ? metric.BuildCacheBytes / (1024d * 1024d * 1024d)
                : null,
            _ => null
        };

    private static bool Compare(double value, ComparisonOperator comparison, double threshold) =>
        comparison switch
        {
            ComparisonOperator.GreaterThan => value > threshold,
            ComparisonOperator.LessThan => value < threshold,
            ComparisonOperator.GreaterThanOrEqual => value >= threshold,
            ComparisonOperator.LessThanOrEqual => value <= threshold,
            _ => false
        };
}
