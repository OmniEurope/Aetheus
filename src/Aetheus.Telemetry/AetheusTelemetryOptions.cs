// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Telemetry;

public sealed class AetheusTelemetryOptions
{
    public bool Enabled { get; set; }
    public string ApplicationName { get; set; } = string.Empty;
    public string? ApplicationVersion { get; set; }
    public string EnvironmentName { get; set; } = "production";
    public int ApplicationId { get; set; }
    public Uri? MetricsEndpoint { get; set; }
    public Uri? LogsEndpoint { get; set; }
    public Uri? TracesEndpoint { get; set; }
    public string IngestKey { get; set; } = string.Empty;
    public bool EnableLogs { get; set; }
    public bool EnableRuntimeMetrics { get; set; } = true;
    public bool EnableHttpClientTracing { get; set; } = true;
    public double TraceSampleRatio { get; set; } = 0.1;
    public int ExportTimeoutMilliseconds { get; set; } = 3000;
    public int MaxExportQueueSize { get; set; } = 2048;
    public int MaxExportBatchSize { get; set; } = 512;

    /// <summary>The optional database size measurement, off until the host enables it.</summary>
    public DatabaseSizeOptions DatabaseSize { get; set; } = new();

    internal void Validate()
    {
        if (!Enabled)
            return;
        ValidateIdentity();
        ValidateEndpoints();
        ValidateExportLimits();
        DatabaseSize.Validate();
    }

    private void ValidateIdentity()
    {
        if (string.IsNullOrWhiteSpace(ApplicationName))
            throw new InvalidOperationException("Aetheus telemetry requires a non-empty application name.");
        if (ApplicationId <= 0)
            throw new InvalidOperationException("Aetheus telemetry requires a positive application id.");
        if (string.IsNullOrWhiteSpace(IngestKey))
            throw new InvalidOperationException("Aetheus telemetry requires an ingestion key.");
    }

    private void ValidateEndpoints()
    {
        if (MetricsEndpoint is null || TracesEndpoint is null || (EnableLogs && LogsEndpoint is null))
            throw new InvalidOperationException(
                "Aetheus telemetry requires metrics and traces endpoints, plus a logs endpoint when logs are enabled.");
        if (MetricsEndpoint.Scheme != Uri.UriSchemeHttps
            || TracesEndpoint.Scheme != Uri.UriSchemeHttps
            || (EnableLogs && LogsEndpoint!.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Aetheus telemetry endpoints must use HTTPS.");
    }

    private void ValidateExportLimits()
    {
        if (TraceSampleRatio is < 0 or > 1)
            throw new InvalidOperationException("TraceSampleRatio must be between 0 and 1.");
        if (ExportTimeoutMilliseconds is < 500 or > 10_000)
            throw new InvalidOperationException("ExportTimeoutMilliseconds must be between 500 and 10000.");
        if (MaxExportQueueSize is < 256 or > 16_384)
            throw new InvalidOperationException("MaxExportQueueSize must be between 256 and 16384.");
        if (MaxExportBatchSize is < 64 || MaxExportBatchSize > MaxExportQueueSize)
            throw new InvalidOperationException("MaxExportBatchSize must be between 64 and MaxExportQueueSize.");
    }
}
