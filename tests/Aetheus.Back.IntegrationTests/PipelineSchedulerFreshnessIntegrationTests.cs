// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// PLAN-005 lot 1, points 1 and 4: how soon the scheduler engine takes a changed schedule into
/// account, and what it costs to find out. The real <c>PipelineSchedulerService</c> runs on a real
/// PostgreSQL database; the clock is moved by hand one check interval (one minute) at a time.
/// </summary>
public sealed class PipelineSchedulerFreshnessIntegrationTests(PostgresFixture fixture)
    : RelationalTestBase(fixture)
{
    // The real timer wakes at arbitrary seconds, not on the minute: a tick exactly on :00 would skip the
    // occurrence of an every-minute schedule (the engine looks one minute back, start included).
    private static readonly DateTimeOffset Start = new(2026, 6, 16, 10, 0, 10, TimeSpan.Zero);

    private const string NightlyAtThree = "name: moved\ntrigger: schedule\nschedule: \"0 0 3 * * *\"\n";
    private const string EveryMinute = "name: moved\ntrigger: schedule\nschedule: \"0 * * * * *\"\n";

    private static string EveryMinuteNamed(string name) => $"name: {name}\ntrigger: schedule\nschedule: \"0 * * * * *\"\n";

    [Fact]
    public async Task ChangedSchedule_IsTakenIntoAccountWithinOneMinute()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        using var harness = new PipelineSchedulerHarness(ConnectionString, Start);
        var id = await harness.AddPipelineAsync("moved", NightlyAtThree, PipelineTriggerType.Schedule, ct);

        // The engine has been awake for a while on the old schedule (03:00 only).
        await harness.TickAsync(ct);
        Assert.Empty(harness.Triggered);

        // Someone saves the pipeline half a minute later: it now fires every minute.
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        var changedAt = harness.Clock.GetUtcNow();
        await harness.SavePipelineAsync(id, EveryMinute, PipelineTriggerType.Schedule, ct);

        // The engine wakes up once a minute, at the same second each time; give it six more ticks,
        // longer than the old five-minute cache.
        for (var minute = 1; minute <= 6 && harness.Triggered.Count == 0; minute++)
            await harness.TickAtAsync(Start.AddMinutes(minute), ct);

        Assert.True(
            harness.Triggered.Count > 0,
            "The engine never took the new schedule into account within six check intervals.");
        var delay = harness.Triggered[0].At - changedAt;
        Assert.Equal(id, harness.Triggered[0].PipelineId);
        Assert.True(delay < TimeSpan.FromMinutes(1), $"The new schedule was seen after {delay} (changed {changedAt:T}, started {harness.Triggered[0].At:T}), not under one minute.");
    }

    [Fact]
    public async Task UnchangedSchedules_CostOneLightweightQuery_AndAreNotReloaded()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        using var harness = new PipelineSchedulerHarness(ConnectionString, Start);
        var id = await harness.AddPipelineAsync("steady", NightlyAtThree, PipelineTriggerType.Schedule, ct);

        var first = await harness.TickAsync(ct);
        var second = await harness.NextMinuteAsync(ct);
        var third = await harness.NextMinuteAsync(ct);

        // First tick loads the pipelines (their YAML is read), the next two do not.
        Assert.Single(first, ReadsDefinitions);
        Assert.DoesNotContain(second, ReadsDefinitions);
        Assert.DoesNotContain(third, ReadsDefinitions);

        // A quiet tick is exactly two queries: the freshness check on the pipelines table and the
        // active-run lookup the engine already did. The freshness check selects aggregates only.
        Assert.Equal(2, second.Count);
        var freshness = Assert.Single(second, FromPipelines);
        Assert.Contains("count(", freshness, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max(", freshness, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("YamlDefinition", freshness, StringComparison.Ordinal);

        // A save does reload, once, and the following quiet tick is light again.
        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await harness.SavePipelineAsync(id, EveryMinute, PipelineTriggerType.Schedule, ct);
        var afterSave = await harness.NextMinuteAsync(ct);
        var afterReload = await harness.NextMinuteAsync(ct);
        Assert.Single(afterSave, ReadsDefinitions);
        Assert.DoesNotContain(afterReload, ReadsDefinitions);
        Assert.Equal(2, afterReload.Count);
    }

    [Fact]
    public async Task DeletedScheduledPipeline_IsNoLongerStarted()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        using var harness = new PipelineSchedulerHarness(ConnectionString, Start);
        var kept = await harness.AddPipelineAsync("kept", EveryMinuteNamed("kept"), PipelineTriggerType.Schedule, ct);
        var removed = await harness.AddPipelineAsync("removed", EveryMinuteNamed("removed"), PipelineTriggerType.Schedule, ct);

        await harness.TickAsync(ct);
        Assert.Equivalent(new[] { kept, removed }, harness.TriggeredAfter(Start.AddSeconds(-1)));

        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await harness.DeletePipelineAsync(removed, ct);
        var before = harness.Clock.GetUtcNow();
        await harness.NextMinuteAsync(ct);

        Assert.Equal([kept], harness.TriggeredAfter(before));
    }

    [Fact]
    public async Task DisabledScheduledPipeline_IsNoLongerStarted()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        using var harness = new PipelineSchedulerHarness(ConnectionString, Start);
        var kept = await harness.AddPipelineAsync("kept", EveryMinuteNamed("kept"), PipelineTriggerType.Schedule, ct);
        var disabled = await harness.AddPipelineAsync("disabled", EveryMinuteNamed("disabled"), PipelineTriggerType.Schedule, ct);

        await harness.TickAsync(ct);

        // Disabling keeps the schedule line and turns the trigger to manual, as the plan prescribes.
        harness.Clock.Advance(TimeSpan.FromSeconds(10));
        await harness.SavePipelineAsync(
            disabled, "name: disabled\ntrigger: manual\nschedule: \"0 * * * * *\"\n", PipelineTriggerType.Manual, ct);
        var before = harness.Clock.GetUtcNow();
        await harness.NextMinuteAsync(ct);

        Assert.Equal([kept], harness.TriggeredAfter(before));
    }

    [Fact]
    public async Task DeletionPairedWithAnEnable_AtTheSameInstant_IsSeen()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAndMigrateAsync();
        using var harness = new PipelineSchedulerHarness(ConnectionString, Start);
        var removed = await harness.AddPipelineAsync("removed", EveryMinuteNamed("removed"), PipelineTriggerType.Schedule, ct);
        var enabled = await harness.AddPipelineAsync(
            "enabled", "name: enabled\ntrigger: manual\nschedule: \"0 * * * * *\"\n", PipelineTriggerType.Manual, ct);
        await harness.TickAsync(ct);
        var before = await harness.FingerprintAsync(ct);

        // Same count (one scheduled pipeline), same latest update (the clock did not move), only the
        // pipeline behind them differs: a count and a latest update alone would see nothing.
        await harness.DeletePipelineAsync(removed, ct);
        await harness.SavePipelineAsync(enabled, EveryMinuteNamed("enabled"), PipelineTriggerType.Schedule, ct);
        var after = await harness.FingerprintAsync(ct);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.LatestUpdate, after.LatestUpdate);
        Assert.NotEqual(before, after);

        var tickedAt = harness.Clock.GetUtcNow();
        await harness.NextMinuteAsync(ct);
        Assert.Equal([enabled], harness.TriggeredAfter(tickedAt));
    }

    private static bool ReadsDefinitions(string statement) =>
        statement.Contains("\"YamlDefinition\"", StringComparison.Ordinal);

    private static bool FromPipelines(string statement) =>
        statement.Contains("FROM \"Pipelines\"", StringComparison.Ordinal);
}
