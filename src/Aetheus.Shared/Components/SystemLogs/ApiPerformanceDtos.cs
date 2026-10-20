// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.SystemLogs;

/// <summary>API response durations measured through the ASP.NET Core request-duration instrument
/// and retained in this instance’s memory.</summary>
public sealed record ApiPerformanceReportDto
{
    /// <summary>Oldest measurement kept. Later than 24 h ago after a restart or a deployment: the
    /// window then starts at the instance start, and the page says so.</summary>
    public DateTime? Since { get; init; }

    public int SampleCount { get; init; }

    /// <summary>True when the cap was reached and the oldest samples of the window were dropped.</summary>
    public bool Truncated { get; init; }

    /// <summary>The slowest individual requests of the window, slowest first.</summary>
    public List<ApiCallTimingDto> Slowest { get; init; } = [];

    /// <summary>One line per route and method, slowest 95th percentile first.</summary>
    public List<ApiEndpointTimingDto> Endpoints { get; init; } = [];
}

public sealed record ApiCallTimingDto
{
    public string Method { get; init; } = string.Empty;

    /// <summary>The route template (<c>api/pipelines/{id}</c>), never the concrete path, so no
    /// identifier or query value is kept.</summary>
    public string Route { get; init; } = string.Empty;

    public int StatusCode { get; init; }
    public double DurationMs { get; init; }
    public DateTime At { get; init; }
}

public sealed record ApiEndpointTimingDto
{
    public string Method { get; init; } = string.Empty;
    public string Route { get; init; } = string.Empty;
    public int Count { get; init; }
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
    public double MaxMs { get; init; }
}
