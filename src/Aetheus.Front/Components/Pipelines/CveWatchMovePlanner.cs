// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>What a drop of the CVE watch block means: <see cref="Refusal"/> (the resource key of the sentence that says why
/// nothing is written), a <see cref="PassTime"/> to write (<c>HH:mm</c> in the zone of the watch), or neither (the drop
/// changes nothing). <see cref="LocalTime"/> is the time of the drop at the reader's clock.</summary>
internal sealed record CveWatchMoveDecision(string? Refusal, string? PassTime, TimeOnly LocalTime, bool DateIgnored);

/// <summary>
/// The pure rules of a drop of the CVE watch block (administrators only). The watch runs once a day at a local time of its own
/// zone (<see cref="ScheduledServerActionDto.DailyTimeZoneId"/>), not at a UTC cron time: the time of the drop is read in
/// that zone and its date is ignored, since every day has the same pass. A reader who may not move it, or a plan that gives
/// no time for it, is refused; a drop that gives the stored time back writes nothing.
/// </summary>
internal static class CveWatchMovePlanner
{
    public static CveWatchMoveDecision Decide(
        ScheduledServerActionDto action, DateTimeOffset originalStart, DateTimeOffset droppedStart, TimeZoneInfo readerZone)
    {
        var dropped = TimeZoneInfo.ConvertTime(droppedStart, readerZone);
        var original = TimeZoneInfo.ConvertTime(originalStart, readerZone);
        var localTime = TimeOnly.FromDateTime(dropped.DateTime);
        var dateIgnored = DateOnly.FromDateTime(dropped.DateTime) != DateOnly.FromDateTime(original.DateTime);
        if (!action.Movable) return new CveWatchMoveDecision("SchedulePermissionDenied", null, localTime, false);
        if (action.DailyTime is null || action.DailyTimeZoneId is null || !TimeZoneInfo.TryFindSystemTimeZoneById(action.DailyTimeZoneId, out var watchZone))
            return new CveWatchMoveDecision("ScheduleMoveCustom", null, localTime, false);

        var passTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(droppedStart, watchZone).DateTime)
            .ToString("HH:mm", CultureInfo.InvariantCulture);
        return new CveWatchMoveDecision(null, passTime == action.DailyTime ? null : passTime, localTime, dateIgnored);
    }
}
