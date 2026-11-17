// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Cronos;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The planner's cron vocabulary. The codec must name a frequency only for the exact shapes it writes
/// itself: a list, an interval or a non-zero second silently read as "every evening" would make the
/// planner rewrite a different schedule than the one the YAML carries.
/// </summary>
public sealed class ScheduleFrequencyCodecTests
{
    [Theory]
    [InlineData(ScheduleFrequency.EveryEvening, null, 2, 30, "0 30 2 * * *")]
    [InlineData(ScheduleFrequency.Weekdays, null, 23, 5, "0 5 23 * * 1-5")]
    [InlineData(ScheduleFrequency.EveryTwoDays, null, 0, 0, "0 0 0 1-31/2 * *")]
    [InlineData(ScheduleFrequency.Weekly, 0, 3, 15, "0 15 3 * * 0")]
    [InlineData(ScheduleFrequency.Weekly, 6, 22, 45, "0 45 22 * * 6")]
    [InlineData(ScheduleFrequency.Monthly, 1, 1, 0, "0 0 1 1 * *")]
    [InlineData(ScheduleFrequency.Monthly, 28, 4, 10, "0 10 4 28 * *")]
    public void EncodeThenDecode_RoundTripsEveryNamedFrequency(ScheduleFrequency frequency, int? day, int hour, int minute, string expectedCron)
    {
        var spec = new ScheduleFrequencySpec(frequency, day, hour, minute);

        var cron = ScheduleFrequencyCodec.Encode(spec);

        Assert.Equal(expectedCron, cron);
        Assert.Equal(spec, ScheduleFrequencyCodec.Decode(cron));
    }

    [Theory]
    [InlineData("0 0 0 * * *", ScheduleFrequency.EveryEvening, null, 0, 0)]
    [InlineData("0 0 2 * * *", ScheduleFrequency.EveryEvening, null, 2, 0)]
    [InlineData("  0   30   1  *  *  *  ", ScheduleFrequency.EveryEvening, null, 1, 30)]
    public void Decode_ReadsTheShippedNightlyAndDemoShapes(string cron, ScheduleFrequency frequency, int? day, int hour, int minute)
    {
        Assert.Equal(new ScheduleFrequencySpec(frequency, day, hour, minute), ScheduleFrequencyCodec.Decode(cron));
    }

    // Unknown expressions are Custom, and Day is never invented. Hour and minute stay readable only
    // when the expression is one time of day at second zero.
    [Theory]
    [InlineData("0 0 2 * * 1-4", 2, 0)]
    [InlineData("0 0 2 * 3 *", 2, 0)]
    [InlineData("0 0 2 1 * 1", 2, 0)]
    [InlineData("0 0 2 29 * *", 2, 0)]
    [InlineData("0 0 2 * * 7", 2, 0)]
    [InlineData("0 0 2 */2 * *", 2, 0)]
    [InlineData("0 0 2 2-31/2 * *", 2, 0)]
    [InlineData("0 0 2 1,15 * *", 2, 0)]
    [InlineData("0 0 2 * * 1,3,5", 2, 0)]
    [InlineData("0 0 2 * * MON", 2, 0)]
    public void Decode_UnknownExpression_IsCustomAndKeepsItsTime(string cron, int hour, int minute)
    {
        var spec = ScheduleFrequencyCodec.Decode(cron);

        Assert.Equal(ScheduleFrequency.Custom, spec.Frequency);
        Assert.Null(spec.Day);
        Assert.Equal(hour, spec.Hour);
        Assert.Equal(minute, spec.Minute);
        Assert.Null(ScheduleFrequencyCodec.Encode(spec));
    }

    [Theory]
    [InlineData("30 0 2 * * *")]
    [InlineData("*/10 0 2 * * *")]
    [InlineData("5 5 5 1-31/2 * *")]
    [InlineData("1 0 2 * * 1-5")]
    public void Decode_NonZeroSeconds_IsCustomWithNoTime(string cron)
    {
        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Custom, null, null, null), ScheduleFrequencyCodec.Decode(cron));
    }

    [Theory]
    [InlineData("0 0,30 2 * * *")]
    [InlineData("0 */15 2 * * *")]
    [InlineData("0 0 2,14 * * *")]
    [InlineData("0 0 */6 * * *")]
    [InlineData("0 0 1-5 * * *")]
    [InlineData("0 * 2 * * *")]
    [InlineData("0 0 * * * *")]
    public void Decode_ListsIntervalsAndWildcardsInTime_AreCustomWithNoTime(string cron)
    {
        var spec = ScheduleFrequencyCodec.Decode(cron);

        Assert.Equal(ScheduleFrequency.Custom, spec.Frequency);
        Assert.Null(spec.Hour);
        Assert.Null(spec.Minute);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0 0 2 * *")]
    [InlineData("0 0 2 * * * *")]
    [InlineData("0 60 2 * * *")]
    [InlineData("0 0 24 * * *")]
    [InlineData("0 0 +2 * * *")]
    [InlineData("0 0 002 * * *")]
    [InlineData("0 -1 2 * * *")]
    public void Decode_MalformedOrOutOfRange_IsCustomNeverANamedFrequency(string cron)
    {
        Assert.Equal(new ScheduleFrequencySpec(ScheduleFrequency.Custom, null, null, null), ScheduleFrequencyCodec.Decode(cron));
    }

    [Fact]
    public void Decode_Null_IsCustom()
    {
        Assert.Equal(ScheduleFrequency.Custom, ScheduleFrequencyCodec.Decode(null).Frequency);
    }

    // The anti-fake proof: across a battery of expressions the planner does not write, no one is
    // ever decoded to anything but Custom, and re-encoding the decoded spec never invents a cron.
    [Theory]
    [InlineData("0 0 2 * * 1-5/2")]
    [InlineData("0 0 2 * * 0-6")]
    [InlineData("0 0 2 1-31 * *")]
    [InlineData("0 0 2 1-31/3 * *")]
    [InlineData("0 0 2 * 1-12 *")]
    [InlineData("0 0 2 * * *,1")]
    [InlineData("0 0 2 ? * *")]
    [InlineData("0 0 2 L * *")]
    [InlineData("0 0 2 * * 5L")]
    [InlineData("0 0 2 15 * 1")]
    public void Decode_NeverMapsAnUnsupportedExpressionToANamedFrequency(string cron)
    {
        var spec = ScheduleFrequencyCodec.Decode(cron);

        Assert.Equal(ScheduleFrequency.Custom, spec.Frequency);
        Assert.Null(ScheduleFrequencyCodec.Encode(spec));
    }

    [Theory]
    [InlineData(ScheduleFrequency.EveryEvening, null, 24, 0)]
    [InlineData(ScheduleFrequency.EveryEvening, null, -1, 0)]
    [InlineData(ScheduleFrequency.EveryEvening, null, 2, 60)]
    [InlineData(ScheduleFrequency.Weekly, null, 2, 0)]
    [InlineData(ScheduleFrequency.Weekly, 7, 2, 0)]
    [InlineData(ScheduleFrequency.Monthly, 0, 2, 0)]
    [InlineData(ScheduleFrequency.Monthly, 29, 2, 0)]
    [InlineData(ScheduleFrequency.Custom, null, 2, 0)]
    public void Encode_RefusesASpecItCannotWrite(ScheduleFrequency frequency, int? day, int hour, int minute)
    {
        Assert.Null(ScheduleFrequencyCodec.Encode(new ScheduleFrequencySpec(frequency, day, hour, minute)));
    }

    [Fact]
    public void Encode_RefusesAMissingTime()
    {
        Assert.Null(ScheduleFrequencyCodec.Encode(new ScheduleFrequencySpec(ScheduleFrequency.EveryEvening, null, null, null)));
    }

    [Fact]
    public void PlannedMinutes_IsOneHour()
    {
        Assert.Equal(60, ScheduleDefaults.PlannedMinutes);
        var task = new ScheduledTaskDto();
        Assert.True(task.IsPlanned);
        Assert.Equal(ScheduleDefaults.PlannedMinutes, task.DurationMinutes);
    }

    // The scheduler parses with CronFormat.IncludeSeconds: what the codec writes must be what the
    // engine fires, not only a string that round-trips through the codec. 2027-01-01 is a Friday.
    [Theory]
    [InlineData(ScheduleFrequency.EveryEvening, null, "2027-01-01T02:30:00Z", "2027-01-02T02:30:00Z")]
    [InlineData(ScheduleFrequency.Weekdays, null, "2027-01-01T02:30:00Z", "2027-01-04T02:30:00Z")]
    [InlineData(ScheduleFrequency.EveryTwoDays, null, "2027-01-01T02:30:00Z", "2027-01-03T02:30:00Z")]
    [InlineData(ScheduleFrequency.Weekly, 1, "2027-01-04T02:30:00Z", "2027-01-11T02:30:00Z")]
    [InlineData(ScheduleFrequency.Monthly, 15, "2027-01-15T02:30:00Z", "2027-02-15T02:30:00Z")]
    public void Encode_WritesWhatTheEngineFires(ScheduleFrequency frequency, int? day, string first, string second)
    {
        var cron = ScheduleFrequencyCodec.Encode(new ScheduleFrequencySpec(frequency, day, 2, 30));
        var expression = CronExpression.Parse(cron!, CronFormat.IncludeSeconds);

        var firstRun = expression.GetNextOccurrence(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc);
        var secondRun = expression.GetNextOccurrence(firstRun!.Value, TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse(first, CultureInfo.InvariantCulture).UtcDateTime, firstRun);
        Assert.Equal(DateTimeOffset.Parse(second, CultureInfo.InvariantCulture).UtcDateTime, secondRun);
    }
}
