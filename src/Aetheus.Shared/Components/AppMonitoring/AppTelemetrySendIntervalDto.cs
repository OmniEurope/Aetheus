// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.AppMonitoring;

/// <summary>
/// How often a supervised application sends its metrics: the interval Aetheus hands to its
/// <c>Aetheus.Telemetry</c> package (« Conservation des données » of its project, PLAN-013 lot 4), and
/// the interval its recent samples actually show. A package older than 1.1.0 does not read the
/// server's interval and keeps its own, which only the observed interval reveals.
/// </summary>
public sealed record AppTelemetrySendIntervalDto
{
    /// <summary>The interval the server hands to the package, in seconds.</summary>
    public int ServerIntervalSeconds { get; init; }

    /// <summary>The data retention profile of the application's project.</summary>
    public DataRetentionProfile Profile { get; init; }

    /// <summary>True when the project sets its own interval instead of its profile's.</summary>
    public bool IsProjectOverride { get; init; }

    /// <summary>The interval the most recent sends show, in seconds; null when the samples are too few
    /// or too irregular to derive one.</summary>
    public int? ObservedIntervalSeconds { get; init; }
}
