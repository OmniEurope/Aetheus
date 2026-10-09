// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The sentences that state how often an application sends its telemetry: « Envoi toutes les 5 min
/// (profil Standard) : un point toutes les 5 minutes », and, when its recent samples show another pace,
/// the interval actually observed.
/// </summary>
public static class TelemetrySendIntervalText
{
    /// <summary>An observed interval this close to the server's is the server's (sends drift by a few seconds).</summary>
    private const double Tolerance = 0.1;
    private const int MinimumToleranceSeconds = 5;

    /// <summary>« Envoi toutes les 5 min (profil Standard) : un point toutes les 5 minutes ».</summary>
    public static string Setting(IStringLocalizer localizer, int seconds, DataRetentionProfile profile, bool isProjectOverride)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var source = isProjectOverride
            ? localizer["TelemetrySendIntervalOverride"].Value
            : localizer["TelemetrySendIntervalProfile", localizer[$"Enum_DataRetentionProfile_{profile}"].Value].Value;
        return localizer["TelemetrySendIntervalSetting", Short(localizer, seconds), source, Long(localizer, seconds)].Value;
    }

    /// <summary>
    /// What the samples say about the server's interval: null when they follow it; the observed interval
    /// when they show another one; otherwise that the server's interval needs Aetheus.Telemetry 1.1.0.
    /// </summary>
    public static string? Observation(IStringLocalizer localizer, AppTelemetrySendIntervalDto interval)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(interval);
        if (interval.ObservedIntervalSeconds is not { } observed)
            return localizer["TelemetrySendIntervalUnobserved"].Value;
        return Follows(interval.ServerIntervalSeconds, observed)
            ? null
            : localizer["TelemetrySendIntervalObservedDiffers", Long(localizer, observed)].Value;
    }

    /// <summary>True when the samples follow the server's interval.</summary>
    public static bool Follows(int serverSeconds, int observedSeconds) =>
        Math.Abs(serverSeconds - observedSeconds) <= Math.Max(MinimumToleranceSeconds, serverSeconds * Tolerance);

    /// <summary>« toutes les 5 min », « chaque minute », « toutes les 45 s ».</summary>
    public static string Short(IStringLocalizer localizer, int seconds)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (seconds == 60)
            return localizer["TelemetryEveryMinuteShort"].Value;
        return seconds % 60 == 0
            ? localizer["TelemetryEveryMinutesShort", Number(seconds / 60)].Value
            : localizer["TelemetryEverySecondsShort", Number(seconds)].Value;
    }

    /// <summary>« toutes les 5 minutes », « par minute », « toutes les 45 secondes ».</summary>
    public static string Long(IStringLocalizer localizer, int seconds)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (seconds == 60)
            return localizer["TelemetryEveryMinuteLong"].Value;
        return seconds % 60 == 0
            ? localizer["TelemetryEveryMinutesLong", Number(seconds / 60)].Value
            : localizer["TelemetryEverySecondsLong", Number(seconds)].Value;
    }

    private static string Number(int value) => value.ToString(CultureInfo.CurrentCulture);
}
