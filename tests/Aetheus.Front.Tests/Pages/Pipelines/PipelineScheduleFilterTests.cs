// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R-8: the planner's "Genre" and "Serveur" filters as pure rules: what the URL may name, which source belongs
/// to which genre, what a filter keeps, and that the CVE watch counts in no tile and no overlap.</summary>
public sealed class PipelineScheduleFilterTests
{
    private static readonly DateTime Now = new(2027, 1, 3, 12, 0, 0, DateTimeKind.Utc);

    private static ScheduledServerActionDto Action(ServerActionKind kind, int sourceId, int serverId = 0, string serverName = "") => new()
    {
        Id = $"{kind}:{sourceId}",
        Kind = kind,
        SourceId = sourceId,
        ServerId = serverId,
        ServerName = serverName,
        NextRunUtc = Now.AddHours(1),
        Occurrences = [new() { StartUtc = Now.AddHours(1), EndUtc = Now.AddHours(2) }]
    };

    private static ScheduledTaskDto Pipeline(int id) => new()
    {
        PipelineId = id,
        PipelineName = $"p{id}",
        Enabled = true,
        NextRunUtc = Now.AddHours(1),
        Occurrences = [new() { StartUtc = Now.AddHours(1), EndUtc = Now.AddHours(2) }]
    };

    private static SchedulePlanDto Plan() => new()
    {
        Tasks = [Pipeline(1), Pipeline(2)],
        ServerActions =
        [
            Action(ServerActionKind.RkhunterScan, 7, 7, "srv-b"),
            Action(ServerActionKind.AiTask, 3),
            Action(ServerActionKind.AppBackup, 5, 8, "srv-a"),
            Action(ServerActionKind.RestoreCheck, 5, 8, "srv-a"),
            Action(ServerActionKind.CveWatch, 0)
        ]
    };

    [Theory]
    [InlineData("pipeline", "pipeline")]
    [InlineData("scan", "scan")]
    [InlineData("ai", "ai")]
    [InlineData("backup", "backup")]
    [InlineData("cve", "cve")]
    [InlineData("Backup", null)]
    [InlineData("restore", null)]
    [InlineData("all", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseKind_KnowsTheFiveGenresOnly(string? value, string? expected) =>
        Assert.Equal(expected, PipelineScheduleFilter.ParseKind(value));

    [Theory]
    [InlineData("7", 7)]
    [InlineData("120", 120)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("+3", null)]
    [InlineData("3.5", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseServer_ReadsAPositiveWholeNumberOnly(string? value, int? expected) =>
        Assert.Equal(expected, PipelineScheduleFilter.ParseServer(value));

    [Theory]
    [InlineData(ServerActionKind.RkhunterScan, "scan")]
    [InlineData(ServerActionKind.AiTask, "ai")]
    [InlineData(ServerActionKind.AppBackup, "backup")]
    [InlineData(ServerActionKind.RestoreCheck, "backup")]
    [InlineData(ServerActionKind.CveWatch, "cve")]
    public void EveryKindOfAction_BelongsToAGenre_TheRestoreCheckWithTheBackups(ServerActionKind kind, string genre) =>
        Assert.Equal(genre, ScheduleGenres.Of(kind));

    [Fact]
    public void EveryKindOfAction_HasAGenreTheBarOffers() =>
        Assert.All(Enum.GetValues<ServerActionKind>(), kind => Assert.Contains(ScheduleGenres.Of(kind), ScheduleGenres.All));

    [Fact]
    public void NoFilter_ReturnsThePlanItself_SoTheCalendarDoesNotLoadAgain()
    {
        var plan = Plan();

        Assert.Same(plan, PipelineScheduleFilter.Apply(plan, null, null));
    }

    [Fact]
    public void ThePipelineGenre_KeepsThePipelinesAndNoAction()
    {
        var filtered = PipelineScheduleFilter.Apply(Plan(), "pipeline", null);

        Assert.Equal(2, filtered.Tasks.Count);
        Assert.Empty(filtered.ServerActions);
    }

    [Theory]
    [InlineData("scan", new[] { "RkhunterScan:7" })]
    [InlineData("ai", new[] { "AiTask:3" })]
    [InlineData("backup", new[] { "AppBackup:5", "RestoreCheck:5" })]
    [InlineData("cve", new[] { "CveWatch:0" })]
    public void AnActionGenre_KeepsItsActionsAndNoPipeline(string kind, string[] expected)
    {
        var filtered = PipelineScheduleFilter.Apply(Plan(), kind, null);

        Assert.Empty(filtered.Tasks);
        Assert.Equal(expected, filtered.ServerActions.Select(action => action.Id));
    }

    [Fact]
    public void AServer_KeepsTheActionsRunningOnIt_AndHidesThePipelinesAndTheActionsOfNoServer()
    {
        var filtered = PipelineScheduleFilter.Apply(Plan(), null, 8);

        Assert.Empty(filtered.Tasks);
        Assert.Equal(["AppBackup:5", "RestoreCheck:5"], filtered.ServerActions.Select(action => action.Id));
    }

    [Fact]
    public void TheKindAndTheServer_Intersect()
    {
        Assert.Equal(
            new[] { "AppBackup:5", "RestoreCheck:5" },
            PipelineScheduleFilter.Apply(Plan(), "backup", 8).ServerActions.Select(action => action.Id).Order(StringComparer.Ordinal));
        Assert.Empty(PipelineScheduleFilter.Apply(Plan(), "scan", 8).ServerActions);
        Assert.Empty(PipelineScheduleFilter.Apply(Plan(), "pipeline", 8).Tasks);
    }

    [Fact]
    public void TheFilter_KeepsTheWindowAndTheOverlapMinutesOfWhatItKeeps()
    {
        var plan = Plan() with { FromUtc = Now, ToUtc = Now.AddDays(7), ExecutionCapacity = 3 };

        var filtered = PipelineScheduleFilter.Apply(plan, "backup", null);

        Assert.Equal(plan.FromUtc, filtered.FromUtc);
        Assert.Equal(plan.ToUtc, filtered.ToUtc);
        Assert.Equal(3, filtered.ExecutionCapacity);
    }

    [Fact]
    public void TheServers_AreTheOnesTheActionsRunOn_OnceEach_ByName()
    {
        var servers = PipelineScheduleFilter.Servers(Plan());

        Assert.Equal([(8, "srv-a"), (7, "srv-b")], servers);
    }

    [Fact]
    public void TheCveWatchEntry_IsOfferedOnlyWhenThePlanHoldsItOrTheUrlNamesIt()
    {
        var withoutCve = Plan() with { ServerActions = [.. Plan().ServerActions.Where(action => action.Kind != ServerActionKind.CveWatch)] };

        Assert.Contains("cve", PipelineScheduleFilter.Genres(Plan(), null));
        Assert.DoesNotContain("cve", PipelineScheduleFilter.Genres(withoutCve, null));
        Assert.DoesNotContain("cve", PipelineScheduleFilter.Genres(withoutCve, "backup"));
        Assert.Contains("cve", PipelineScheduleFilter.Genres(withoutCve, "cve"));
    }

    [Fact]
    public void TheCveWatch_CountsInNoTileAndNoOverlap_ButTheOtherActionsDo()
    {
        var summary = SchedulePlanSummary.Build(Plan(), TimeZoneInfo.Utc, Now);

        // Two pipelines, a scan, an AI task, a backup and a restore check; never the CVE watch.
        Assert.Equal(6, summary.EnabledCount);
        Assert.False(summary.HasOverlap(ServerActionPlannerTask.RowId(Action(ServerActionKind.CveWatch, 0))));
    }

    [Theory]
    [InlineData(ServerActionKind.RkhunterScan, true, true)]
    [InlineData(ServerActionKind.AiTask, true, true)]
    [InlineData(ServerActionKind.AppBackup, true, true)]
    [InlineData(ServerActionKind.RestoreCheck, false, true)]
    [InlineData(ServerActionKind.CveWatch, false, false)]
    public void WhatThePlannerMayMoveAndCount_FollowsTheSortOfAction(ServerActionKind kind, bool canBeMoved, bool countsInOverlaps)
    {
        Assert.Equal(canBeMoved, ServerActionDefaults.CanBeMoved(kind));
        Assert.Equal(countsInOverlaps, ServerActionDefaults.CountsInOverlaps(kind));
    }

    [Fact]
    public void TheRowsOfEveryKindOfAction_NeverMeet()
    {
        var rows = Enum.GetValues<ServerActionKind>().Select(kind => ServerActionPlannerTask.RowId(Action(kind, 7))).ToList();

        Assert.Equal(rows.Count, rows.Distinct().Count());
        Assert.All(rows, row => Assert.True(row < 0));
    }
}
