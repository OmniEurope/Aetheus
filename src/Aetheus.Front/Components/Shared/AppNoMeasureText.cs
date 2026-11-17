// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Shared;

/// <summary>The server measure an empty view waits for, which decides the wording of the one cause that depends on it.</summary>
internal enum AppMeasureSignal { Performance, Database }

/// <summary>
/// R-27: the sentence an empty Performance or Database view shows to say why no server measure ever arrived. The cause is read by
/// the server (<see cref="AppNoMeasureDto"/>); this only words it, with the date of the last reception when there is one. Null when
/// the server gave no cause, so the view keeps its general hint rather than an invented reason.
/// </summary>
internal static class AppNoMeasureText
{
    public static string? Describe(IStringLocalizer<AppStrings> localizer, AppNoMeasureDto? reason, AppMeasureSignal signal)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (reason is null) return null;

        var cause = reason.Cause switch
        {
            AppNoMeasureCause.NoIngestKey => localizer["NoMeasureNoIngestKey"].Value,
            AppNoMeasureCause.AppDisabled => localizer["NoMeasureAppDisabled"].Value,
            AppNoMeasureCause.KeyExpired => reason.CauseAt is { } expired
                ? string.Format(CultureInfo.CurrentCulture, localizer["NoMeasureKeyExpired"].Value, Date(expired))
                : localizer["NoMeasureKeyExpiredNoDate"].Value,
            AppNoMeasureCause.KeyRefused => string.Format(
                CultureInfo.CurrentCulture, localizer["NoMeasureKeyRefused"].Value, Date(reason.CauseAt)),
            AppNoMeasureCause.NothingReceived => localizer["NoMeasureNothingReceived"].Value,
            _ => localizer[signal == AppMeasureSignal.Performance ? "NoMeasureMissingPerformance" : "NoMeasureMissingDatabase"].Value
        };

        return reason.LastReceptionAt is { } last
            ? $"{cause} {string.Format(CultureInfo.CurrentCulture, localizer["NoMeasureLastReception"].Value, Date(last))}"
            : cause;
    }

    private static string Date(DateTime? value) =>
        value?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "-";
}
