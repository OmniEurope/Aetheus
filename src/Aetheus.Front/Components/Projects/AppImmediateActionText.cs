// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Projects;

/// <summary>A sentence and a severity for one immediate action result (R-29).</summary>
internal sealed record AppImmediateActionMessage(OmniSeverity Severity, string Text);

/// <summary>
/// R-29: what the Supervision says about the last immediate probe and the last immediate telemetry request of an
/// application, always with the time of each step. A result is said only when it arrived after the request; a request
/// that is waiting says so, and one whose answer did not come says what is likely missing.
/// </summary>
internal static class AppImmediateActionText
{
    internal static AppImmediateActionMessage? Probe(IStringLocalizer<AppStrings> L, AppProbeNowDto? state)
    {
        if (state?.RequestedAt is not { } requestedAt)
            return null;
        if (state.Result is { } result)
        {
            var at = Time(result.Timestamp);
            if (result.IsUp)
                return new(OmniSeverity.Success, Format(L["AppProbeNowUp"], result.StatusCode?.ToString(CultureInfo.CurrentCulture) ?? "-",
                    result.ResponseTimeMs?.ToString(CultureInfo.CurrentCulture) ?? "-", at));
            var reason = result.StatusCode is { } status
                ? Format(L["AppProbeNowStatus"], status.ToString(CultureInfo.CurrentCulture))
                : AppProbeFailureText.Describe(L, result.Error);
            return new(OmniSeverity.Danger, Format(L["AppProbeNowDown"], reason, at));
        }

        return state.TimedOut
            ? new(OmniSeverity.Warning, Format(L["AppProbeNowTimedOut"], Time(requestedAt)))
            : new(OmniSeverity.Info, Format(L["AppProbeNowPending"], Time(requestedAt)));
    }

    internal static AppImmediateActionMessage? Telemetry(IStringLocalizer<AppStrings> L, AppTelemetryExportRequestDto? state)
    {
        if (state?.RequestedAt is not { } requestedAt)
            return null;
        var (severity, text) = (state.ReceivedAt, state.DeliveredAt, state.TimedOut) switch
        {
            ({ } received, { } delivered, _) => (OmniSeverity.Success,
                Format(L["AppTelemetryNowReceived"], Time(received), Time(requestedAt), Time(delivered))),
            // The package did not take the request, but its own periodic export reached Aetheus since the click: the
            // telemetry does arrive, only the immediate request is unanswered (a package older than 1.2.2).
            (null, null, true) when state.DataReceivedAt is { } data => (OmniSeverity.Success,
                Format(L["AppTelemetryNowDataButNotTaken"], Time(data), Time(requestedAt))),
            (null, null, true) => (OmniSeverity.Warning, Format(L["AppTelemetryNowNotTaken"], Time(requestedAt))),
            (null, { } delivered, true) => (OmniSeverity.Warning, Format(L["AppTelemetryNowNotReceived"], Time(delivered))),
            (null, { } delivered, false) => (OmniSeverity.Info, Format(L["AppTelemetryNowTaken"], Time(delivered))),
            _ => (OmniSeverity.Info, Format(L["AppTelemetryNowPending"], Time(requestedAt)))
        };
        var answered = state.ReceivedAt is not null || (state.TimedOut && state.DeliveredAt is null && state.DataReceivedAt is not null);
        if (!answered)
            text = $"{text} {DataSentence(L, state)}";
        var visits = !state.AnalyticsEnabled
            ? L["AppTelemetryNowAudienceOff"].Value
            : state.VisitsReceivedAt is { } visit
                ? Format(L["AppTelemetryNowVisits"], Time(visit))
                : L["AppTelemetryNowNoVisit"].Value;
        return new(severity, $"{text} {visits}");
    }

    /// <summary>What reached Aetheus since the click, or the last reading from before it, or that nothing ever came.</summary>
    private static string DataSentence(IStringLocalizer<AppStrings> L, AppTelemetryExportRequestDto state) =>
        state.DataReceivedAt is { } since ? Format(L["AppTelemetryNowDataSince"], Time(since))
        : state.LastIngestAt is { } last ? Format(L["AppTelemetryNowDataBefore"], Time(last))
        : L["AppTelemetryNowNeverData"].Value;

    /// <summary>The local date and time, seconds included: the user compares it with the moment of the click.</summary>
    internal static string Time(DateTime value) =>
        (value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value)
        .ToLocalTime().ToString("G", CultureInfo.CurrentCulture);

    private static string Format(string pattern, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, pattern, values);
}
