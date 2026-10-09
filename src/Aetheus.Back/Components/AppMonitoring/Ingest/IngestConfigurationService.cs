// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AppMonitoring.Ingest;

public sealed class IngestConfigurationService(IAppDataRetentionRepository retention) : IIngestConfigurationService
{
    public async Task<IngestConfigurationDto?> GetAsync(int appId, CancellationToken ct = default)
    {
        var values = await retention.GetForAppAsync(appId, ct).ConfigureAwait(false);
        return values is null
            ? null
            : new IngestConfigurationDto(values.TelemetryExportIntervalMinutes * 60);
    }
}
