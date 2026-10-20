// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.AppMonitoring;

/// <summary> The configuration an application's <c>Aetheus.Telemetry</c> package reads with its
/// ingest key (<c>GET api/ingest/otlp/v1/configuration</c>). A package that does not read it keeps
/// its own interval. </summary>
public sealed record IngestConfigurationDto(int MetricExportIntervalSeconds);
