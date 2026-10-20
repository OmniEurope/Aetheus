// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Caching.Memory;

namespace Aetheus.Back.Components.AppMonitoring.Ingest;

/// <summary>Tells the open views of a monitored app that its telemetry changed.</summary>
public interface IAppTelemetryChangePublisher
{
    /// <summary>Pushes <see cref="OperationalRealtimeEvents.AppTelemetryChanged"/> for the app's project.</summary>
    Task PublishAsync(int appId, CancellationToken ct = default);
}

/// <summary> The Logs and Errors tabs of a monitored app reload when an OTLP batch stored new rows,
/// instead of waiting for a Refresh click. The event rides the scoped EntityHub feed on the owning
/// Project (the id carried is the project id), so only connections allowed to read that project -
/// the same permission the telemetry endpoints check - receive it; the payload holds no telemetry.
/// The app-to-project mapping is cached briefly because this runs once per ingestion request.
/// </summary>
public sealed class AppTelemetryChangePublisher(
    IAppMonitoringRepository apps,
    IEntityChangeNotifier notifier,
    IMemoryCache cache,
    ILogger<AppTelemetryChangePublisher> logger) : IAppTelemetryChangePublisher
{
    private static readonly TimeSpan OwnerCacheDuration = TimeSpan.FromMinutes(5);

    private readonly record struct AppOwner(int ProjectId, int? OrganizationId);

    public async Task PublishAsync(int appId, CancellationToken ct = default)
    {
        try
        {
            var owner = await ResolveOwnerAsync(appId, ct).ConfigureAwait(false);
            if (owner is not { } resolved) return;
            await notifier.BroadcastOperationalAsync(
                ResourceType.Project,
                resolved.ProjectId,
                OperationalRealtimeEvents.AppTelemetryChanged,
                ct,
                resolved.OrganizationId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mandatory catch: the batch is already stored and acknowledged to the emitter; a lost push
            // only leaves the open views on their previous page until the next batch, so it must not
            // turn a successful ingestion into an error the exporter would retry (duplicating rows).
            logger.LogWarning(ex, "App telemetry change push failed for monitored app {AppId}", appId);
        }
    }

    private async Task<AppOwner?> ResolveOwnerAsync(int appId, CancellationToken ct)
    {
        var key = $"app-telemetry-owner:{appId}";
        if (cache.TryGetValue<AppOwner>(key, out var cached)) return cached;

        var projectId = await apps.GetAppProjectIdAsync(appId, ct).ConfigureAwait(false);
        if (projectId is not { } id) return null;
        var orgIds = await apps.GetProjectOrgIdsAsync([id], ct).ConfigureAwait(false);
        var owner = new AppOwner(id, orgIds.TryGetValue(id, out var orgId) ? orgId : null);
        cache.Set(key, owner, OwnerCacheDuration);
        return owner;
    }
}
