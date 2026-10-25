// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Alerts;

/// <summary>
/// Ensures every enrolled server has the baseline storage guards (disk usage, free space, inodes)
/// and the two clock offset guards. Existing rules are never re-enabled or overwritten, so an operator can still disable or
/// customize a provisioned rule.
/// </summary>
public sealed class StorageAlertProvisioningService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<StorageAlertProvisioningService> logger,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromHours(1);

    // Only the live colour runs it (decision of 2026-10-02, PostgresLeaderLease).
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync("aetheus:storage-alert-provisioning", RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        do
        {
            try
            {
                await ProvisionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not provision baseline storage alert rules");
            }

            await Task.Delay(ReconcileInterval, stoppingToken).ConfigureAwait(false);
        } while (!stoppingToken.IsCancellationRequested);
    }

    internal async Task ProvisionAsync(CancellationToken ct)
    {
        var provisionStorage = configuration.GetValue("StorageMonitoring:ProvisionDefaultAlerts", true);
        var provisionClock = configuration.GetValue("ClockMonitoring:ProvisionDefaultAlerts", true);
        if (!provisionStorage && !provisionClock) return;

        var warningPercent = Math.Clamp(configuration.GetValue("StorageMonitoring:DiskWarningPercent", 80d), 1d, 100d);
        var criticalPercent = Math.Clamp(configuration.GetValue("StorageMonitoring:DiskCriticalPercent", 90d), warningPercent, 100d);
        var minimumFreeGiB = Math.Max(1d, configuration.GetValue("StorageMonitoring:MinFreeGiB", 20d));
        var inodeWarningPercent = Math.Clamp(configuration.GetValue("StorageMonitoring:InodeWarningPercent", 80d), 1d, 100d);
        var inodeCriticalPercent = Math.Clamp(configuration.GetValue("StorageMonitoring:InodeCriticalPercent", 90d), inodeWarningPercent, 100d);
        var clockWarningSeconds = Math.Max(0.1d, configuration.GetValue("ClockMonitoring:WarningSeconds", 1d));
        var clockCriticalSeconds = Math.Max(clockWarningSeconds, configuration.GetValue("ClockMonitoring:CriticalSeconds", 30d));

        await using var scope = scopeFactory.CreateAsyncScope();
        var alertRepository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
        var servers = await alertRepository.GetServerIdNamePairsAsync(ct).ConfigureAwait(false);
        var provisioningKeys = new HashSet<string>(StringComparer.Ordinal);
        var candidateRules = new List<AlertRule>();

        foreach (var server in servers)
        {
            if (provisionClock)
            {
                // PLAN-010 lot 1: an agent whose clock drifts signs tokens, logs and schedules on a time
                // the rest of the fleet does not share. The rule reads the offset the measure
                // guarantees; a server without a clock sample never triggers it (unknown is silent).
                AddCandidate(provisioningKeys, candidateRules, $"clock-offset-warning:{server.Id}", server.Id,
                    $"Horloge décalée ≥ {clockWarningSeconds:0.#} s - {server.Name}",
                    MetricType.ClockOffset, ComparisonOperator.GreaterThanOrEqual, clockWarningSeconds, 300, AlertSeverity.Warning);
                AddCandidate(provisioningKeys, candidateRules, $"clock-offset-critical:{server.Id}", server.Id,
                    $"Horloge décalée ≥ {clockCriticalSeconds:0.#} s - {server.Name}",
                    MetricType.ClockOffset, ComparisonOperator.GreaterThanOrEqual, clockCriticalSeconds, 300, AlertSeverity.Critical);
            }
            if (!provisionStorage) continue;

            AddCandidate(provisioningKeys, candidateRules, $"storage-disk-warning:{server.Id}", server.Id,
                $"Stockage ≥ {warningPercent:0.#} % - {server.Name}",
                MetricType.Disk, ComparisonOperator.GreaterThanOrEqual, warningPercent, 300, AlertSeverity.Warning);
            AddCandidate(provisioningKeys, candidateRules, $"storage-disk-critical:{server.Id}", server.Id,
                $"Stockage ≥ {criticalPercent:0.#} % - {server.Name}",
                MetricType.Disk, ComparisonOperator.GreaterThanOrEqual, criticalPercent, 60, AlertSeverity.Critical);
            AddCandidate(provisioningKeys, candidateRules, $"storage-disk-free:{server.Id}", server.Id,
                $"Stockage libre < {minimumFreeGiB:0.#} Go - {server.Name}",
                MetricType.DiskFree, ComparisonOperator.LessThan, minimumFreeGiB, 60, AlertSeverity.Critical);
            // PLAN-010 lot 1: a filesystem out of inodes refuses writes with free space left, so the
            // inode table gets the same two thresholds as the disk. A server whose agent does not
            // report inodes never triggers them (unknown is not a breach).
            AddCandidate(provisioningKeys, candidateRules, $"storage-inodes-warning:{server.Id}", server.Id,
                $"Nombre de fichiers ≥ {inodeWarningPercent:0.#} % - {server.Name}",
                MetricType.Inodes, ComparisonOperator.GreaterThanOrEqual, inodeWarningPercent, 300, AlertSeverity.Warning);
            AddCandidate(provisioningKeys, candidateRules, $"storage-inodes-critical:{server.Id}", server.Id,
                $"Nombre de fichiers ≥ {inodeCriticalPercent:0.#} % - {server.Name}",
                MetricType.Inodes, ComparisonOperator.GreaterThanOrEqual, inodeCriticalPercent, 60, AlertSeverity.Critical);
        }

        var inserted = await alertRepository
            .AddProvisionedRulesIfMissingAsync(candidateRules, ct)
            .ConfigureAwait(false);
        if (inserted > 0)
            logger.LogInformation("Provisioned {Count} baseline storage alert rule(s)", inserted);
    }

    private static void AddCandidate(
        ISet<string> provisioningKeys,
        ICollection<AlertRule> candidateRules,
        string provisioningKey,
        int serverId,
        string name,
        MetricType metric,
        ComparisonOperator comparison,
        double threshold,
        int sustainedSeconds,
        AlertSeverity severity)
    {
        if (!provisioningKeys.Add(provisioningKey)) return;

        candidateRules.Add(new AlertRule
        {
            Name = name,
            ProvisioningKey = provisioningKey,
            ServerId = serverId,
            Metric = metric,
            Operator = comparison,
            Threshold = threshold,
            SustainedSeconds = sustainedSeconds,
            Severity = severity,
            IsEnabled = true
        });
    }
}
