// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.DailyRecap;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// Decides once a day whether the daily CVE mail (<see cref="CveWatchDigest"/>) is due (recette R-25). It follows the
/// daily recap's own hour and time zone, five minutes later (07:05 Europe/Paris unless an administrator changed the
/// recap settings): the recap says what is still open, this mail comes right after it with what is new (user
/// decision of 2026-11-03), so the platform still has one hour to set for its daily mails. The recap being switched off does not
/// stop it: a person turns this mail off with their notification preference for <c>analysis.cve.detected</c>.
/// The day is claimed before the send, so a restart, a second instance or a failure never sends a day twice; a
/// backend that was down at the hour sends it when it comes back the same day.
/// </summary>
internal sealed class CveWatchDigestSchedule(
    IDailyRecapRepository settingsRepository,
    CveWatchDigest digest,
    TimeProvider timeProvider,
    ILogger<CveWatchDigestSchedule> logger)
{
    /// <summary>How long after the daily recap the CVE mail leaves, so it reads as its follow-up.</summary>
    internal static readonly TimeSpan DelayAfterRecap = TimeSpan.FromMinutes(5);

    public async Task<DailyRecapScheduleOutcome> RunScheduledAsync(CancellationToken ct)
    {
        var rows = await settingsRepository.GetSettingRowsAsync(ct).ConfigureAwait(false);
        var settings = DailyRecapSettings.Parse(rows);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (!DailyRecapClock.TryResolveTimeZone(settings.TimeZoneId, out var timeZone))
        {
            logger.LogWarning("[CveWatchDigest] timezone {TimeZone} is unknown on this host; UTC is used", settings.TimeZoneId);
            timeZone = TimeZoneInfo.Utc;
        }

        var decision = DailyRecapClock.Decide(now, enabled: true, settings.SendTime.Add(DelayAfterRecap), timeZone, LastSentDate(rows));
        if (decision.Outcome != DailyRecapScheduleOutcome.Due) return decision.Outcome;

        // The day is claimed before anything is sent: whatever happens next, it is not sent twice.
        await settingsRepository.SaveSettingRowsAsync(new Dictionary<string, string>
        {
            [DailyRecapSettingKeys.CveDigestLastSentDate] = decision.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        }, now, ct).ConfigureAwait(false);
        try
        {
            await digest.SendAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The day is claimed and will not be retried; the matches told for the first time are given back.
            logger.LogError(exception, "[CveWatchDigest] the nightly CVE mail of {Day} could not be sent", decision.Today);
        }

        return decision.Outcome;
    }

    private static DateOnly? LastSentDate(IReadOnlyDictionary<string, string> rows) =>
        rows.TryGetValue(DailyRecapSettingKeys.CveDigestLastSentDate, out var text)
        && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;
}
