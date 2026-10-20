// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring.Ingest;
using Aetheus.Back.Components.Vaults;

namespace Aetheus.Back.Components.AppMonitoring;

public sealed class AppDeployEnvProvider(
    IAppMonitoringRepository appRepo,
    IngestKeyHasher hasher,
    ISecretMaskingService masking,
    IIngestService ingestService,
    IAuditService audit,
    IVaultService vaults,
    IConfiguration configuration,
    TimeProvider timeProvider) : IAppDeployEnvProvider
{
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    /// <summary>
    /// Every name this provider can emit. The control plane derives them for the step that starts a
    /// colour and for no other, so the blue-green binding drops them from <c>compose_env</c> on every
    /// other step rather than refusing a name that step was never meant to receive (D-01).
    /// </summary>
    internal static readonly string[] DerivedNames =
    [
        "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
        "OTEL_EXPORTER_OTLP_PROTOCOL", "OTEL_EXPORTER_OTLP_HEADERS", "OTEL_SERVICE_NAME",
        "AETHEUS_VISITOR_ENDPOINT", "AETHEUS_INGEST_KEY",
        "AETHEUS_TELEMETRY_ENABLED", "AETHEUS_TELEMETRY_LOGS_ENABLED", "AETHEUS_TELEMETRY_APPLICATION_ID",
        "AETHEUS_WEB_ANALYTICS_ENABLED", "AETHEUS_WEB_ANALYTICS_APPLICATION_ID", "AETHEUS_WEB_ANALYTICS_SITE_ID",
        "AETHEUS_WEB_ANALYTICS_INGEST_ENDPOINT", "AETHEUS_WEB_ANALYTICS_INGEST_KEY",
        "AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY", "AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY_VERSION"
    ];

    public async Task<IReadOnlyDictionary<string, string>> GetDeployEnvAsync(
        int projectId, int? environmentId, int pipelineRunId, CancellationToken ct = default)
    {
        // Feature is opt-in per environment: without a configured public OTLP base URL there is nowhere
        // for the deployed app to push, so we inject nothing (a bogus endpoint would be worse than none).
        var baseUrl = configuration["AppMonitoring:IngestBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            return None;

        var app = await appRepo.GetDeployTargetAppAsync(projectId, environmentId, ct).ConfigureAwait(false);
        if (app is null)
            return None;

        // Deployment credentials are derived once per run so repeated stages and backend restarts
        // retain the same key. Rotation preserves both blue-green colours' overlapping keys until
        // their existing expiry deadlines.
        var plaintext = hasher.DeriveForRun(app.Id, pipelineRunId);
        if (!string.Equals(app.IngestKeyHash, hasher.Hash(plaintext), StringComparison.Ordinal))
        {
            var rotation = IngestKeyRotation.ApplyForDeploy(
                app, plaintext, hasher, configuration, timeProvider);
            await appRepo.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var hash in rotation.AffectedHashes)
                ingestService.InvalidateKeyCache(hash);
            await audit.LogAsync(
                rotation.PreviousHash is null ? "CreatedIngestKeyForDeploy" : "RotatedIngestKeyForDeploy",
                "MonitoredApp",
                app.Id,
                $"Version {app.IngestKeyVersion}; pipeline run {pipelineRunId}; previous valid until {app.PreviousIngestKeyValidUntil:O}; "
                + $"second previous valid until {app.SecondPreviousIngestKeyValidUntil:O}",
                ct).ConfigureAwait(false);
        }

        // Mask the plaintext in this run's step logs (it will appear in the env dispatch).
        masking.RegisterRuntimeSecret(pipelineRunId, plaintext);

        var endpoint = baseUrl.TrimEnd('/') + "/api/ingest/otlp/v1";
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Signal-specific endpoints are exact URLs. A common OTLP HTTP endpoint would append
            // /v1/{signal} and duplicate the route already present in Aetheus.
            ["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"] = endpoint + "/metrics",
            ["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = endpoint + "/logs",
            ["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] = endpoint + "/traces",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = $"x-aetheus-ingest-key={plaintext}",
            ["AETHEUS_VISITOR_ENDPOINT"] = baseUrl.TrimEnd('/') + "/api/ingest/visitors",
            ["AETHEUS_INGEST_KEY"] = plaintext,
            ["AETHEUS_TELEMETRY_ENABLED"] = "true",
            ["AETHEUS_TELEMETRY_LOGS_ENABLED"] = "true",
            ["AETHEUS_TELEMETRY_APPLICATION_ID"] = app.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["OTEL_SERVICE_NAME"] = app.Name
        };

        // Web analytics reaches the front through the same compose_env contract as telemetry, and that
        // contract refuses a listed variable the run does not define rather than letting Compose fall
        // back to its own default. The whole set is therefore always emitted: when the feature is not
        // configured the two key values are inert placeholders and AETHEUS_WEB_ANALYTICS_ENABLED stays
        // "false", which stops the browser module before it ever reads them.
        const string AnalyticsDisabledPlaceholder = "disabled";
        var analyticsPseudonymizationKey = AnalyticsDisabledPlaceholder;
        var analyticsIngestKey = AnalyticsDisabledPlaceholder;
        var analyticsEnabled = false;

        if (app.AnalyticsEnabled && app.AnalyticsVaultName is not null)
        {
            var secrets = await vaults.ResolveVaultSecretsAsync(
                [app.AnalyticsVaultName],
                app.ProjectId,
                ct).ConfigureAwait(false);
            if (AppWebAnalyticsConfigurationService.ReadKey(secrets, app.Id) is { } pseudonymizationKey)
            {
                masking.RegisterRuntimeSecret(pipelineRunId, pseudonymizationKey);
                analyticsPseudonymizationKey = pseudonymizationKey;
                analyticsIngestKey = plaintext;
                analyticsEnabled = true;
            }
        }

        result["AETHEUS_WEB_ANALYTICS_ENABLED"] = analyticsEnabled ? "true" : "false";
        result["AETHEUS_WEB_ANALYTICS_APPLICATION_ID"] = app.Id.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        result["AETHEUS_WEB_ANALYTICS_SITE_ID"] = app.AnalyticsSiteId ?? $"app-{app.Id}";
        result["AETHEUS_WEB_ANALYTICS_INGEST_ENDPOINT"] =
            baseUrl.TrimEnd('/') + "/api/ingest/web-analytics/v1/events";
        result["AETHEUS_WEB_ANALYTICS_INGEST_KEY"] = analyticsIngestKey;
        result["AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY"] = analyticsPseudonymizationKey;
        result["AETHEUS_WEB_ANALYTICS_PSEUDONYMIZATION_KEY_VERSION"] =
            app.AnalyticsPseudonymKeyVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture);

        return result;
    }
}
