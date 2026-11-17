// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Each occurrence of a schedule starts at most once, across ticks, restarts and instances, and an
/// occurrence missed during an outage is caught up once. The real <c>PipelineSchedulerService</c> runs
/// on a real PostgreSQL database; a "restart" is a second service instance on the same database, and
/// the clock is moved by hand. Only the run service is a double. The engine works in UTC (cron
/// occurrences are computed from UTC instants), so a daylight-saving change cannot move or repeat one.
/// </summary>
public sealed class PipelineSchedulerCatchUpIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    private const string EveryMinute = "0 * * * * *";
    private const string EveryTenMinutes = "0 */10 * * * *";
    private const string NightlyAtTwoOhFive = "0 5 2 * * *";
    private const string HourlyOnTheHour = "0 0 * * * *";

    private static string Yaml(string name, string cron) =>
        $"name: {name}\ntrigger: schedule\nschedule: \"{cron}\"\n";

    private static DateTimeOffset At(int hour, int minute, int second, int millisecond = 0) =>
        new(2026, 6, 16, hour, minute, second, millisecond, TimeSpan.Zero);

    private async Task<(PipelineSchedulerHarness Harness, int Id)> StartAsync(
        string cron, DateTimeOffset start, CancellationToken ct)
    {
        await ResetAndMigrateAsync();
        var harness = new PipelineSchedulerHarness(ConnectionString, start);
        var id = await harness.AddPipelineAsync("scheduled", Yaml("scheduled", cron), PipelineTriggerType.Schedule, ct);
        return (harness, id);
    }

    [Fact]
    public async Task TwoTicksInTheSameMinute_StartAnOccurrenceOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, _) = await StartAsync(EveryMinute, At(10, 0, 10), ct);
        using var scoped = harness;

        // The check interval may be shorter than a minute: four ticks, two occurrences (10:00 and 10:01).
        foreach (var tick in new[] { At(10, 0, 10), At(10, 0, 40), At(10, 1, 10), At(10, 1, 40) })
            await harness.TickAtAsync(tick, ct);

        Assert.Equal(2, harness.Triggered.Count);
    }

    [Fact]
    public async Task OccurrenceSkippedForARunInFlight_IsNotStartedWhenTheRunEndsBeforeTheNextTick()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, id) = await StartAsync(EveryMinute, At(10, 0, 10), ct);
        using var scoped = harness;
        var runId = await harness.AddActiveRunAsync(id, ct);

        await harness.TickAtAsync(At(10, 0, 10), ct);
        harness.Clock.Set(At(10, 0, 25));
        await harness.EndRunAsync(runId, ct);
        await harness.TickAtAsync(At(10, 0, 40), ct);

        Assert.Empty(harness.Triggered);
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("still active", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OccurrenceExactlyOnTheTickInstant_IsStarted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, _) = await StartAsync(EveryMinute, At(10, 0, 0), ct);
        using var scoped = harness;

        foreach (var tick in new[] { At(10, 0, 0), At(10, 1, 0), At(10, 2, 0) })
            await harness.TickAtAsync(tick, ct);

        Assert.Equal([At(10, 0, 0), At(10, 1, 0), At(10, 2, 0)], harness.Triggered.Select(call => call.At));
    }

    [Fact]
    public async Task RestartBetweenTicks_DoesNotStartTheSameOccurrenceTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        var (first, _) = await StartAsync(EveryMinute, At(10, 0, 10), ct);
        await first.TickAsync(ct);
        first.Dispose();

        using var restarted = new PipelineSchedulerHarness(ConnectionString, At(10, 0, 40));
        await restarted.TickAsync(ct);
        await restarted.TickAtAsync(At(10, 1, 10), ct);

        Assert.Single(first.Triggered);
        Assert.Equal([At(10, 1, 10)], restarted.Triggered.Select(call => call.At));
    }

    [Fact]
    public async Task OccurrenceInsideAShortOutage_IsCaughtUpOnce_AndMarkedAsCatchUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var (first, id) = await StartAsync(NightlyAtTwoOhFive, At(2, 4, 10), ct);
        await first.TickAsync(ct);
        first.Dispose();

        // The process is down over 02:05; the next one is up at 02:07:10.
        using var restarted = new PipelineSchedulerHarness(ConnectionString, At(2, 7, 10));
        await restarted.TickAsync(ct);
        await restarted.TickAtAsync(At(2, 8, 10), ct);

        var call = Assert.Single(restarted.Calls);
        Assert.Equal(id, call.PipelineId);
        Assert.Equal("Scheduler", call.Source);
        Assert.NotNull(call.Variables);
        Assert.Equal(PipelineSchedulerService.CatchUpTriggeredBy, call.Variables[PipelineSchedulerService.TriggeredByVariable]);
        Assert.Equal(
            At(2, 5, 0).UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            call.Variables[PipelineSchedulerService.OccurrenceVariable]);
    }

    [Fact]
    public async Task OutageLongerThanTheMaximum_IsNotRun_ButLoggedAtInformation_AndNotRepeated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (first, _) = await StartAsync(NightlyAtTwoOhFive, At(2, 4, 10), ct);
        await first.TickAsync(ct);
        first.Dispose();

        using var restarted = new PipelineSchedulerHarness(
            ConnectionString, At(2, 5, 0) + PipelineSchedulerService.MaxCatchUpAge + TimeSpan.FromMinutes(1));
        await restarted.TickAsync(ct);
        await restarted.NextMinuteAsync(ct);
        await restarted.NextMinuteAsync(ct);

        Assert.Empty(restarted.Triggered);
        var logged = restarted.Logger.Entries.Where(entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("catch-up limit", StringComparison.Ordinal)).ToList();
        Assert.Single(logged);
    }

    [Fact]
    public async Task ManyMissedOccurrences_StartOneRunOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (first, id) = await StartAsync(EveryTenMinutes, At(10, 0, 10), ct);
        await first.TickAsync(ct);
        first.Dispose();

        // 10:10, 10:20, 10:30, 10:40 and 10:50 are all missed; the next instance is up at 10:55:10.
        using var restarted = new PipelineSchedulerHarness(ConnectionString, At(10, 55, 10));
        await restarted.TickAsync(ct);
        await restarted.NextMinuteAsync(ct);
        await restarted.NextMinuteAsync(ct);

        var call = Assert.Single(restarted.Calls);
        Assert.Equal(id, call.PipelineId);
        Assert.Equal(
            At(10, 50, 0).UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            call.Variables![PipelineSchedulerService.OccurrenceVariable]);
    }

    [Fact]
    public async Task TwoInstancesOnOneDatabase_StartEachOccurrenceOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        using var one = new PipelineSchedulerHarness(ConnectionString, At(10, 0, 10));
        using var other = new PipelineSchedulerHarness(ConnectionString, At(10, 0, 10));
        var ids = new List<int>();
        for (var index = 0; index < 3; index++)
            ids.Add(await one.AddPipelineAsync($"p{index}", Yaml($"p{index}", EveryMinute), PipelineTriggerType.Schedule, ct));

        // Both instances wake up at the same instants and race for every occurrence of every pipeline.
        for (var minute = 0; minute < 6; minute++)
        {
            var instant = At(10, 0, 10).AddMinutes(minute);
            await Task.WhenAll(one.TickAtAsync(instant, ct), other.TickAtAsync(instant, ct));
        }

        var starts = one.Triggered.Concat(other.Triggered)
            .GroupBy(call => (call.PipelineId, call.At))
            .ToList();
        Assert.All(starts, group => Assert.Single(group));
        Assert.Equal(ids.Count * 6, starts.Count);
    }

    [Fact]
    public async Task LeaderChange_WithAGapBetweenTheTwoLeaders_StartsTheLastOccurrenceOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (former, _) = await StartAsync(EveryMinute, At(10, 0, 10), ct);
        await former.TickAsync(ct);
        await former.NextMinuteAsync(ct);
        former.Dispose();

        // Nobody is leader for 10:02 and 10:03; the new leader wakes up at 10:04:10.
        using var successor = new PipelineSchedulerHarness(ConnectionString, At(10, 4, 10));
        await successor.TickAsync(ct);
        await successor.NextMinuteAsync(ct);

        Assert.Equal(2, former.Triggered.Count);
        Assert.Equal([At(10, 4, 10), At(10, 5, 10)], successor.Triggered.Select(call => call.At));
    }

    [Fact]
    public async Task RefusedOccurrence_CountsAsHandled_AndIsNotRetriedInTheSameMinute()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, _) = await StartAsync(EveryMinute, At(10, 0, 10), ct);
        using var scoped = harness;
        harness.Failure = _ => new BadRequestException("invalid matrix");

        await harness.TickAtAsync(At(10, 0, 10), ct);
        await harness.TickAtAsync(At(10, 0, 40), ct);
        Assert.Single(harness.Calls);

        await harness.TickAtAsync(At(10, 1, 10), ct);
        Assert.Equal(2, harness.Calls.Count);
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnexpectedFailure_ReleasesTheOccurrence_SoTheNextTickStartsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (harness, _) = await StartAsync(EveryMinute, At(10, 0, 10), ct);
        using var scoped = harness;
        harness.Failure = _ => new InvalidOperationException("database unreachable");

        // R-30: the failure is contained in the tick (one failing pipeline never stops the others), and the
        // occurrence is still given back, which the two ticks below prove.
        await harness.TickAtAsync(At(10, 0, 10), ct);
        Assert.Single(harness.Calls);
        harness.Failure = null;
        await harness.TickAtAsync(At(10, 0, 40), ct);
        await harness.TickAtAsync(At(10, 0, 50), ct);

        Assert.Equal(2, harness.Calls.Count);
        Assert.Equal(At(10, 0, 40), harness.Calls[1].At);
    }

    [Fact]
    public async Task ScheduleSavedAfterALongIdle_DoesNotCatchUpTheOldDefinition()
    {
        var ct = TestContext.Current.CancellationToken;
        var (first, id) = await StartAsync(NightlyAtTwoOhFive, At(2, 4, 10), ct);
        await first.TickAsync(ct);
        first.Dispose();

        // At 10:30:20 the nightly job becomes hourly: its 09:00 and 10:00 occurrences never belonged to it.
        using var restarted = new PipelineSchedulerHarness(ConnectionString, At(10, 30, 20));
        await restarted.SavePipelineAsync(id, Yaml("scheduled", HourlyOnTheHour), PipelineTriggerType.Schedule, ct);
        await restarted.TickAtAsync(At(10, 31, 10), ct);
        Assert.Empty(restarted.Triggered);

        await restarted.TickAtAsync(At(11, 0, 10), ct);
        Assert.Single(restarted.Triggered);
        Assert.Null(restarted.Calls[0].Variables);
    }
}
