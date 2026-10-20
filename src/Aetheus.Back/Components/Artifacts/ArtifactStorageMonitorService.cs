// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Artifacts;

/// <summary>Measure physical artifact storage and raise throttled budget or growth alerts without
/// deleting data. Persist growth history and reload it on leadership acquisition so deployments do
/// not reset the observation window.</summary>
public sealed class ArtifactStorageMonitorService(
    IConfiguration configuration,
    IHubContext<AlertHub> alertHub,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ArtifactStorageMonitorService> logger,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    /// <summary>Only the leader measures and alerts to avoid duplicate notifications across
    /// replicas.</summary>
    internal const string LeaseName = "aetheus:artifact-storage-monitor";

    /// <summary>Measure growth over at least this window to avoid extrapolating a single large
    /// upload into daily growth.</summary>
    internal static readonly TimeSpan GrowthWindow = TimeSpan.FromHours(24);

    /// <summary>A growth over the threshold is a risk only when it would reach the budget within this
    /// horizon (or when no budget is configured).</summary>
    internal static readonly TimeSpan BudgetHorizon = TimeSpan.FromDays(7);

    private readonly List<(DateTime At, long Bytes)> _history = [];
    private bool _historyLoaded;
    private DateTime? _lastAlertAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (leaderLease is null)
            await RunLeaderLoopAsync(stoppingToken).ConfigureAwait(false);
        else
            await leaderLease.RunAsLeaderAsync(LeaseName, RunLeaderLoopAsync, stoppingToken).ConfigureAwait(false);
    }

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        // A new leadership reloads the persisted history: the other colour may have measured meanwhile.
        _historyLoaded = false;
        do
        {
            try
            {
                await EvaluateAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not monitor artifact storage");
            }

            var minutes = Math.Clamp(configuration.GetValue("ArtifactStorage:MonitorIntervalMinutes", 60), 5, 1440);
            await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken).ConfigureAwait(false);
        } while (!stoppingToken.IsCancellationRequested);
    }

    internal async Task EvaluateAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var measurements = scope.ServiceProvider.GetRequiredService<IArtifactStorageMeasurementRepository>();
        if (!_historyLoaded)
        {
            _history.Clear();
            _history.AddRange(await measurements.GetAsync(ct).ConfigureAwait(false));
            _historyLoaded = true;
        }

        var basePath = Path.GetFullPath(configuration["ArtifactStorage:BasePath"] ?? "./data/artifacts");
        var currentBytes = MeasureDirectoryBytes(basePath);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var budgetBytes = Math.Max(0, configuration.GetValue("ArtifactStorage:VolumeBudgetBytes", 53_687_091_200L));
        var growthWarningBytesPerDay = Math.Max(0, configuration.GetValue("ArtifactStorage:GrowthWarningBytesPerDay", 5_368_709_120L));

        var growthPerDay = GrowthBytesPerDay(_history, now, currentBytes);
        var daysUntilBudget = DaysUntilBudget(currentBytes, budgetBytes, growthPerDay);

        logger.LogInformation(
            "Artifact storage inventory: path={Path}, bytes={Bytes}, budgetBytes={BudgetBytes}, growthBytesPerDay={GrowthBytesPerDay}, daysUntilBudget={DaysUntilBudget}",
            basePath, currentBytes, budgetBytes, growthPerDay, daysUntilBudget);

        var overBudget = budgetBytes > 0 && currentBytes >= budgetBytes;
        var growingTooFast = growthWarningBytesPerDay > 0 && growthPerDay >= growthWarningBytesPerDay
            && (budgetBytes == 0 || daysUntilBudget <= BudgetHorizon.TotalDays);
        var alertCooldown = TimeSpan.FromHours(6);
        if ((overBudget || growingTooFast)
            && (_lastAlertAt is null || now - _lastAlertAt >= alertCooldown))
        {
            _lastAlertAt = now;
            var cause = overBudget ? "budget dépassé" : "croissance trop rapide";
            logger.LogWarning(
                "Artifact storage alert: {Cause}; bytes={Bytes}, budgetBytes={BudgetBytes}, growthBytesPerDay={GrowthBytesPerDay}, daysUntilBudget={DaysUntilBudget}",
                cause, currentBytes, budgetBytes, growthPerDay, daysUntilBudget);
            await alertHub.Clients.Group(HubGroups.Alerts).SendAsync("AlertTriggered", new AlertTriggeredDto
            {
                RuleName = "ArtifactStorage",
                Metric = "ArtifactVolumeBytes",
                Severity = overBudget ? "Critical" : "Warning",
                Message = $"Stockage des artefacts : {cause} ({currentBytes} octets).",
                Threshold = overBudget ? budgetBytes : growthWarningBytesPerDay,
                TriggeredAt = now
            }, ct).ConfigureAwait(false);
        }

        Record(_history, now, currentBytes);
        await measurements.RecordAsync(now, currentBytes, now - GrowthWindow, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Growth per day between the newest measurement at least <see cref="GrowthWindow"/> old and now; null
    /// until the history spans that window. A shrinking volume counts as no growth.
    /// </summary>
    internal static double? GrowthBytesPerDay(IReadOnlyList<(DateTime At, long Bytes)> history, DateTime now, long currentBytes)
    {
        (DateTime At, long Bytes)? baseline = null;
        foreach (var measurement in history)
        {
            if (now - measurement.At >= GrowthWindow)
                baseline = measurement;
        }
        if (baseline is not { } reference) return null;
        return Math.Max(0, currentBytes - reference.Bytes) / (now - reference.At).TotalDays;
    }

    /// <summary>Days before the budget is reached at the measured growth; null without a budget or a growth.</summary>
    internal static double? DaysUntilBudget(long currentBytes, long budgetBytes, double? growthBytesPerDay)
    {
        if (budgetBytes <= 0 || growthBytesPerDay is not > 0) return null;
        return Math.Max(0, budgetBytes - currentBytes) / growthBytesPerDay.Value;
    }

    /// <summary>Appends the measurement and keeps only what a later growth needs: the measurements of the
    /// last <see cref="GrowthWindow"/> and the newest one older than it.</summary>
    private static void Record(List<(DateTime At, long Bytes)> history, DateTime now, long bytes)
    {
        history.Add((now, bytes));
        while (history.Count > 1 && now - history[1].At >= GrowthWindow)
            history.RemoveAt(0);
    }

    internal static long MeasureDirectoryBytes(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(path));
        while (pending.TryPop(out var directory))
        {
            foreach (var file in directory.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) == 0)
                    total = checked(total + file.Length);
            }
            foreach (var child in directory.EnumerateDirectories())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) == 0)
                    pending.Push(child);
            }
        }
        return total;
    }
}
