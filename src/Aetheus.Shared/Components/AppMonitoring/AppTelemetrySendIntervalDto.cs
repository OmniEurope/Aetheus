// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.AppMonitoring;

/// <summary>Reports both the configured project telemetry interval and the interval observed in
/// samples. Packages predating 1.1.0 retain their own interval instead of reading server
/// configuration.</summary>
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
