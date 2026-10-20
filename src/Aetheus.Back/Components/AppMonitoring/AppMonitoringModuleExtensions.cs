// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Sockets;
using Aetheus.Back.Components.AppMonitoring.Ingest;

namespace Aetheus.Back.Components.AppMonitoring;

public static class AppMonitoringModuleExtensions
{
    /// <summary>Named HttpClient for backend black-box probing of off-fleet apps (public URLs only).</summary>
    public const string ProbeHttpClientName = "app-probe";

    public static IServiceCollection AddAppMonitoringModule(this IServiceCollection services)
    {
        services.AddScoped<IAppMonitoringRepository, AppMonitoringRepository>();
        services.AddScoped<IAppMonitoringService, AppMonitoringService>();
        services.AddScoped<IAppMetricRepository, AppMetricRepository>();
        services.AddScoped<IAppLogRepository, AppLogRepository>();
        services.AddScoped<IAppDataRetentionRepository, AppDataRetentionRepository>();
        services.AddScoped<IAppErrorRepository, AppErrorRepository>();
        services.AddScoped<IAppVisitorRepository, AppVisitorRepository>();
        services.AddScoped<IAppWebAnalyticsRepository, AppWebAnalyticsRepository>();
        services.AddScoped<IAppWebAnalyticsService, AppWebAnalyticsService>();
        services.AddScoped<IAppWebAnalyticsConfigurationService, AppWebAnalyticsConfigurationService>();
        services.AddScoped<IAppTelemetryChangePublisher, AppTelemetryChangePublisher>();
        services.AddScoped<IIngestService, IngestService>();
        services.AddScoped<IIngestConfigurationService, IngestConfigurationService>();
        services.AddScoped<IVisitorIngestService, VisitorIngestService>();
        services.AddScoped<IAppTelemetryService, AppTelemetryService>();
        services.AddScoped<IAppTelemetrySendIntervalService, AppTelemetrySendIntervalService>();
        services.AddScoped<IAppDeployEnvProvider, AppDeployEnvProvider>();
        services.AddSingleton<IngestKeyHasher>();
        // Singleton so a refused key is reported once per period across request scopes.
        services.AddSingleton<IngestKeyRejectionLog>();
        services.AddSingleton<AppAnalyticsSiteRateLimiter>();
        // Singleton so the per-app metric-ingest lock is shared across request scopes.
        services.AddSingleton<AppIngestGate>();
        services.AddHostedService<AppProbeService>();
        services.AddHostedService<AppTelemetryRetentionService>();

        // SSRF-hardened probe client: no redirects, no cookies, and connect-time IP revalidation so a
        // DNS-rebind between validation and connect still cannot reach a private/loopback address.
        services.AddHttpClient(ProbeHttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Aetheus-AppProbe/1.0");
        }).ConfigurePrimaryHttpMessageHandler(_ => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = async (ctx, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct).ConfigureAwait(false);
                var safe = addresses.FirstOrDefault(a => !WebhookSsrfGuard.IsForbiddenAddress(a))
                    ?? throw new HttpRequestException("Probe target resolved to a forbidden (private/loopback) address.");

                var socket = new Socket(safe.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(safe, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        });

        return services;
    }
}
