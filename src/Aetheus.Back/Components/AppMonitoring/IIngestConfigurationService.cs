// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AppMonitoring;

/// <summary> What the <c>Aetheus.Telemetry</c> package of an application reads with its ingest key,
/// today the metric send interval of its project's « Conservation des données » setting. </summary>
public interface IIngestConfigurationService
{
    Task<IngestConfigurationDto?> GetAsync(int appId, CancellationToken ct = default);
}
