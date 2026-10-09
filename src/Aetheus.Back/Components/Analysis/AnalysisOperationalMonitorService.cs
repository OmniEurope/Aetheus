// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Text.Json;
using Aetheus.Back.Components.Notifications;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Analysis;

public sealed class AnalysisOperationalMonitorService(
    IServiceScopeFactory scopeFactory,
    IOptions<AnalysisPlatformOptions> options,
    TimeProvider timeProvider,
    ILogger<AnalysisOperationalMonitorService> logger,
    IPostgresLeaderLease? leaderLease = null) : BackgroundService
{
    private readonly AnalysisPlatformOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new(StringComparer.Ordinal);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        leaderLease is null
            ? RunLeaderLoopAsync(stoppingToken)
            : leaderLease.RunAsLeaderAsync("aetheus:analysis-operational-monitor", RunLeaderLoopAsync, stoppingToken);

    private async Task RunLeaderLoopAsync(CancellationToken stoppingToken)
    {
        await RunCycleSafelyAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(Math.Clamp(_options.OperationalCheckIntervalMinutes, 5, 1440)), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await RunCycleSafelyAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task RunCycleSafelyAsync(CancellationToken ct)
    {
        try
        {
            await RunCycleAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Analysis operational health cycle failed");
        }

        // PLAN-013 lot 2: the analysis detail retention rides the same hourly leader loop, apart from the
        // health cycle so a failure of one never skips the other.
        try
        {
            await PurgeExpiredDetailAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Analysis detail retention failed");
        }
    }

    internal async Task<AnalysisDetailPurgeResult> PurgeExpiredDetailAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AnalysisDetailRetentionService>()
            .PurgeAsync(timeProvider.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
    }

    internal async Task RunCycleAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAnalysisRepository>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var snapshot = await repository.GetOperationalSnapshotAsync(now.UtcDateTime.AddDays(-1), ct).ConfigureAwait(false);
        foreach (var issue in DetectIssues(snapshot, ScannerManifestCatalog.Default, _options, now))
        {
            var cooldown = TimeSpan.FromHours(Math.Clamp(_options.OperationalAlertCooldownHours, 1, 168));
            if (_lastSent.TryGetValue(issue.Key, out var lastSent) && now - lastSent < cooldown) continue;
            await notifications.SendEventAsync(issue.EventType, new
            {
                issue.Key,
                issue.OrganizationId,
                issue.ProjectId,
                issue.ServerId,
                issue.Message,
                ObservedAt = now.UtcDateTime
            }, ct).ConfigureAwait(false);
            _lastSent[issue.Key] = now;
            logger.LogWarning("Analysis operational alert {EventType}: {Message}", issue.EventType, issue.Message);
        }
    }

    internal static IReadOnlyList<AnalysisOperationalIssue> DetectIssues(
        AnalysisOperationalSnapshot snapshot,
        ScannerManifest manifest,
        AnalysisPlatformOptions options,
        DateTimeOffset now)
    {
        var issues = new List<AnalysisOperationalIssue>();
        AddManifestIssue(issues, manifest, options, now);
        AddTrackingIssues(issues, snapshot, options, now);
        AddServerIssues(issues, snapshot);
        AddStorageIssues(issues, snapshot, options);
        return issues;
    }

    private static void AddManifestIssue(
        ICollection<AnalysisOperationalIssue> issues,
        ScannerManifest manifest,
        AnalysisPlatformOptions options,
        DateTimeOffset now)
    {
        if (manifest.UpdatedAt != default
            && now - new DateTimeOffset(DateTime.SpecifyKind(manifest.UpdatedAt, DateTimeKind.Utc))
                > TimeSpan.FromDays(Math.Clamp(options.ScannerManifestMaxAgeDays, 1, 365)))
        {
            issues.Add(new("scanner-manifest-stale", "analysis.operational.scanner-obsolete", null, null, null,
                $"Scanner manifest has not been reviewed since {manifest.UpdatedAt:O}."));
        }
    }

    private static void AddTrackingIssues(
        ICollection<AnalysisOperationalIssue> issues,
        AnalysisOperationalSnapshot snapshot,
        AnalysisPlatformOptions options,
        DateTimeOffset now)
    {
        var syncStale = TimeSpan.FromHours(Math.Clamp(options.ContinuousSyncStaleHours, 1, 720));
        foreach (var tracking in snapshot.TrackingProjects)
        {
            if (string.Equals(tracking.SyncStatus, "Failed", StringComparison.OrdinalIgnoreCase))
                issues.Add(new($"cve-sync-failed:{tracking.ProjectId}", "analysis.operational.cve-sync-failed",
                    tracking.OrganizationId, tracking.ProjectId, null,
                    tracking.LastError ?? "Dependency-Track synchronization failed."));
            else if (!tracking.LastSyncAt.HasValue || now.UtcDateTime - tracking.LastSyncAt.Value > syncStale)
                issues.Add(new($"cve-sync-stale:{tracking.ProjectId}", "analysis.operational.cve-db-stale",
                    tracking.OrganizationId, tracking.ProjectId, null,
                    "Continuous vulnerability data is stale or has never synchronized."));
        }
    }

    private static void AddServerIssues(
        ICollection<AnalysisOperationalIssue> issues,
        AnalysisOperationalSnapshot snapshot)
    {
        foreach (var server in snapshot.Servers)
        {
            foreach (var capability in ReadCapabilities(server.ScannerCapabilitiesJson))
            {
                if (capability.StartsWith("scanner-cleanup:degraded:", StringComparison.Ordinal))
                    issues.Add(new($"scanner-workspace:{server.ServerId}", "analysis.operational.workspace-residual",
                        server.OrganizationId, null, server.ServerId, capability));
                else if (capability.StartsWith("scanner-db:trivy:degraded:", StringComparison.Ordinal))
                    issues.Add(new($"trivy-db:{server.ServerId}", "analysis.operational.cve-db-stale",
                        server.OrganizationId, null, server.ServerId, capability));
                else if (capability.StartsWith("scanner:", StringComparison.Ordinal)
                         && (capability.Contains(":degraded:", StringComparison.Ordinal)
                             || capability.Contains(":unavailable:", StringComparison.Ordinal))
                         && !IsPermanentlyOutOfScope(capability))
                    issues.Add(new($"scanner-capability:{server.ServerId}:{capability.Split(':')[1]}",
                        "analysis.operational.scanner-unavailable", server.OrganizationId, null, server.ServerId, capability));
            }
        }
    }

    /// <summary>
    /// A scanner the host can never run, as opposed to one that is merely broken right now. The probe
    /// reports both as <c>unavailable</c>, but only one of them is worth waking an operator: a Windows
    /// or arm64 runner will not grow x64 Linux support, so the alert would repeat every cooldown with
    /// nothing to be done about it, and an alert nobody can act on is what teaches people to ignore
    /// the rest. The capability is still reported on the server, so the fact stays visible.
    /// </summary>
    private static bool IsPermanentlyOutOfScope(string capability) =>
        capability.EndsWith(":verified binary supports Linux x64 only", StringComparison.Ordinal);

    private static void AddStorageIssues(
        ICollection<AnalysisOperationalIssue> issues,
        AnalysisOperationalSnapshot snapshot,
        AnalysisPlatformOptions options)
    {
        var byteThreshold = options.ProjectDailyBytesLimit * Math.Clamp(options.StorageWarningPercent, 1, 100) / 100;
        var countThreshold = options.ProjectDailyReportLimit * Math.Clamp(options.StorageWarningPercent, 1, 100) / 100;
        foreach (var storage in snapshot.Storage.Where(item =>
                     item.ContentBytes >= byteThreshold || item.ReportCount >= countThreshold))
            issues.Add(new($"analysis-storage:{storage.ProjectId}", "analysis.operational.storage-drift",
                storage.OrganizationId, storage.ProjectId, null,
                $"Analysis ingestion reached {storage.ReportCount} reports and {storage.ContentBytes} bytes over 24 hours."));
    }

    private static IEnumerable<string> ReadCapabilities(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { yield break; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) yield break;
            foreach (var value in document.RootElement.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } capability)
                    yield return capability;
        }
    }
}

public sealed record AnalysisOperationalIssue(
    string Key,
    string EventType,
    int? OrganizationId,
    int? ProjectId,
    int? ServerId,
    string Message);
