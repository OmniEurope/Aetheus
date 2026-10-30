// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// That the chart actually renders what the layout computed. The placement rules are pinned in
/// <c>RunGanttChartTests</c>; what these add is that the numbers reach the markup, since a bar whose
/// position never makes it into the style is invisible and the component would look empty while
/// reporting a full layout.
/// </summary>
public class RunGanttStripTests : BunitContext
{
    private static readonly DateTime Origin = new(2026, 9, 8, 10, 0, 0, DateTimeKind.Utc);

    public RunGanttStripTests() => BunitTestHelper.RegisterServices(this);

    private static PipelineStepRunDto Step(
        int id, string stage, int? startSeconds, int? endSeconds,
        TaskExecutionStatus status = TaskExecutionStatus.Success, int? depth = null) => new()
        {
            Id = id,
            StageName = stage,
            StepName = $"step {id}",
            Status = status,
            StartedAt = startSeconds is { } s ? Origin.AddSeconds(s) : null,
            CompletedAt = endSeconds is { } e ? Origin.AddSeconds(e) : null,
            StageDepth = depth
        };

    private IRenderedComponent<RunGanttStrip> Render(params PipelineStepRunDto[] steps)
    {
        var clock = new FakeTimeProvider(Origin.AddSeconds(1000));
        return Render<RunGanttStrip>(parameters => parameters
            .Add(p => p.Run, new PipelineRunDto { Id = 1, Steps = [.. steps] })
            .Add(p => p.Clock, clock));
    }

    [Fact]
    public void EachStageGetsOneBarCarryingItsPositionAndWidth()
    {
        var component = Render(
            Step(1, "Compile", 0, 100),
            Step(2, "Test", 100, 200));

        var bars = component.FindAll(".run-gantt-bar");
        Assert.Equal(2, bars.Count);
        Assert.Equal("50", bars[1].GetAttribute("x"));
        Assert.Equal("50", bars[1].GetAttribute("width"));
    }

    [Fact]
    public void PositionsUseAnInvariantDecimalSeparator()
    {
        // A French culture would render "33,333%" and CSS would drop the declaration, leaving every
        // bar stacked at the left edge with no error anywhere.
        var component = Render(Step(1, "A", 0, 100), Step(2, "B", 100, 300));

        var width = component.FindAll(".run-gantt-bar")[0].GetAttribute("width")!;
        Assert.DoesNotContain(",", width, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunningStageIsMarkedAsSuch()
    {
        var component = Render(
            Step(1, "Compile", 0, 100),
            Step(2, "Test", 100, null, TaskExecutionStatus.Running));

        Assert.Single(component.FindAll(".run-gantt-running"));
    }

    [Fact]
    public void AStageThatNeverStartedIsNamedInsteadOfDrawn()
    {
        var component = Render(
            Step(1, "Compile", 0, 100),
            Step(2, "Deploy", null, null, TaskExecutionStatus.Pending));

        Assert.Single(component.FindAll(".run-gantt-bar"));
        Assert.Contains("Deploy", component.Find(".run-gantt-untimed").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunWhereNothingStartedRendersNothingAtAll()
    {
        var component = Render(Step(1, "Waiting", null, null, TaskExecutionStatus.Pending));

        Assert.Empty(component.FindAll(".run-gantt"));
    }

    [Fact]
    public void TheAxisIsLabelledWithTheRunsTotalDuration()
    {
        var component = Render(Step(1, "Compile", 0, 125));

        Assert.Contains("2min", component.Find(".run-gantt-scale").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAxisIsGraduatedInQuartersWithALineThroughEveryRow()
    {
        // Quarter-axis labels.
        var component = Render(Step(1, "Compile", 0, 100), Step(2, "Test", 100, 200));

        Assert.Equal(
            ["0s", "50s", "1min40s", "", "3min20s"], // R-40: "2min30s" would run into "3min20s"
            component.FindAll(".run-gantt-tick").Select(tick => tick.TextContent));
        Assert.All(component.FindAll(".run-gantt-svg"), svg =>
            Assert.Equal(["25", "50", "75"], svg.QuerySelectorAll(".run-gantt-gridline").Select(line => line.GetAttribute("x1"))));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(70)]
    [InlineData(125)]
    [InlineData(300)]
    [InlineData(3_600)]
    public void TwoConsecutiveAxisTicksNeverShareALabel(int seconds)
    {
        var component = Render(Step(1, "Compile", 0, seconds));

        var labels = component.FindAll(".run-gantt-tick").Select(tick => tick.TextContent).Where(text => text.Length > 0).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
        Assert.True(labels.Count >= 2);
    }

    /// <summary>R-23, seen on run 1343: a 21-minute axis read "0s | 5min19s | 10min | 15min | 21min". One axis
    /// reads in one precision, whole minutes from ten minutes, seconds below.</summary>
    [Theory]
    [InlineData(1_276, "0s|5min|10min|15min|21min")]
    [InlineData(200, "0s|50s|1min40s||3min20s")]
    public void TheAxisReadsInOnePrecision(int seconds, string expected)
    {
        var component = Render(Step(1, "Compile", 0, seconds));

        Assert.Equal(expected, string.Join('|', component.FindAll(".run-gantt-tick").Select(tick => tick.TextContent.Trim())));
    }

    /// <summary>R-40, seen on a 1h47 run: "1h20min" (75 %, centred) and "1h47min" (100 %, right-aligned) overlapped.
    /// The end label wins and the 75 % one is left empty; short labels all stay.</summary>
    [Theory]
    [InlineData(6_420, "0s|26min|53min||1h47min")]
    [InlineData(1_276, "0s|5min|10min|15min|21min")]
    public void NoTwoAxisLabelsOverlap(int seconds, string expected)
    {
        var component = Render(Step(1, "Compile", 0, seconds));

        Assert.Equal(expected, string.Join('|', component.FindAll(".run-gantt-tick").Select(tick => tick.TextContent.Trim())));
    }

    [Fact]
    public void AnOverlappingLabelIsEmptiedAndTheEndLabelIsKept()
    {
        var ticks = RunGanttStrip.DropOverlappingLabels(
            [new(0, "0s"), new(25, "26min"), new(50, "53min"), new(75, "1h20min"), new(100, "1h47min")]);

        Assert.Equal(["0s", "26min", "53min", "", "1h47min"], ticks.Select(tick => tick.Label));
    }

    [Fact]
    public void AClickOnAStageBandScrollsToTheDetailedTimeline()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var component = Render<RunGanttStrip>(parameters => parameters
            .Add(p => p.Run, new PipelineRunDto { Id = 1, Steps = [Step(1, "Compile", 0, 100)] })
            .Add(p => p.Clock, new FakeTimeProvider(Origin.AddSeconds(1000)))
            .Add(p => p.ScrollTargetId, "run-timeline-zone"));

        component.Find(".run-gantt-track").Click();

        var call = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "Aetheus.scrollToId");
        Assert.Equal("run-timeline-zone", call.Arguments[0]);
    }

    [Fact]
    public void WithoutAScrollTargetTheBandsStayInert()
    {
        var component = Render(Step(1, "Compile", 0, 100));

        component.Find(".run-gantt-track").Click();

        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "Aetheus.scrollToId");
        Assert.Null(component.Find(".run-gantt-track").GetAttribute("role"));
    }

    [Fact]
    public void AStageThatWaitedGetsAHatchedSegmentEndingWhereItsBarBegins()
    {
        // 50 s between Compile's end and Test's start.
        var component = Render(
            Step(1, "Compile", 0, 100, depth: 0),
            Step(2, "Test", 150, 200, depth: 1));

        var wait = component.Find(".run-gantt-wait");
        Assert.Equal("50", wait.GetAttribute("x"));
        Assert.Equal("25", wait.GetAttribute("width"));
        var patternId = component.Find(".run-gantt-hatch").GetAttribute("id");
        Assert.Equal($"url(#{patternId})", wait.GetAttribute("fill"));
        Assert.Equal("75", component.FindAll(".run-gantt-bar")[1].GetAttribute("x"));
    }

    [Fact]
    public void TheWaitColumnAppearsOnlyWhenAStageWaited()
    {
        // Same rule as the other columns.
        var noWait = Render(Step(1, "Compile", 0, 100, depth: 0), Step(2, "Test", 100, 200, depth: 1));
        Assert.DoesNotContain(noWait.FindAll(".run-gantt-head .run-gantt-cols > span"),
            span => span.GetAttribute("data-column") == "Wait");

        var waited = Render(Step(1, "Compile", 0, 100, depth: 0), Step(2, "Test", 130, 200, depth: 1));
        Assert.Contains(waited.FindAll(".run-gantt-head .run-gantt-cols > span"),
            span => span.GetAttribute("data-column") == "Wait");
        Assert.Equal("30s", waited.FindAll(".run-gantt-col[data-column='Wait']")[1].TextContent);
    }

    [Fact]
    public void EachRowCarriesItsStatusAsAnIconAndNotOnlyAsAColour()
    {
        // Success, failed, cancelled and running each get their own shape.
        var component = Render(
            Step(1, "A", 0, 10),
            Step(2, "B", 10, 20, TaskExecutionStatus.Failed),
            Step(3, "C", 20, 30, TaskExecutionStatus.Cancelled),
            Step(4, "D", 30, null, TaskExecutionStatus.Running));

        var icons = component.FindAll(".run-gantt-status-icon");
        Assert.Equal(4, icons.Count);
        Assert.Equal(4, icons.Select(icon => icon.QuerySelector("path")!.GetAttribute("d")).Distinct().Count());
        Assert.All(icons, icon => Assert.False(string.IsNullOrEmpty(icon.GetAttribute("aria-label"))));
    }

    [Fact]
    public void TheTotalLineAddsExecutionAndWaitAndGivesTheRunDuration()
    {
        // 12 s of execution and 37 s of wait over a 49 s axis; the run itself, measured like the
        // Started tile, lasted from its own start to its end.
        var clock = new FakeTimeProvider(Origin.AddSeconds(1000));
        var component = Render<RunGanttStrip>(parameters => parameters
            .Add(p => p.Run, new PipelineRunDto
            {
                Id = 1,
                StartedAt = Origin.AddSeconds(-2),
                CompletedAt = Origin.AddSeconds(50),
                Steps =
                [
                    Step(1, "Prepare", 0, 3),
                    Step(2, "Verify", 10, 14, depth: 0),
                    Step(3, "Deploy", 20, 25, depth: 1),
                    Step(4, "Cleanup", 49, 49)
                ]
            })
            .Add(p => p.Clock, clock));

        string Total(string kind) =>
            component.Find($".run-gantt-total-item[data-total='{kind}'] .run-gantt-total-value").TextContent;
        Assert.Equal("12s", Total("execution"));
        Assert.Equal("37s", Total("wait"));
        Assert.Equal("52s", Total("run"));
    }

    [Fact]
    public void AStageRowUnfoldsIntoItsStepsAndFoldsBack()
    {
        // Collapsed by default; the button carries aria-expanded and, once pressed, the stage's
        // steps appear as thinner bars on the same axis, with only Duration filled.
        var component = Render(
            Step(1, "Compile", 0, 100),
            Step(2, "Test", 100, 150),
            Step(3, "Test", 150, 200, TaskExecutionStatus.Failed),
            // Cancelled before it ever started: an end is recorded, a start is not.
            Step(4, "Test", null, 200, TaskExecutionStatus.Cancelled));

        var toggles = component.FindAll("button.run-gantt-toggle");
        Assert.Equal(2, toggles.Count);
        Assert.All(toggles, toggle => Assert.Equal("false", toggle.GetAttribute("aria-expanded")));
        Assert.Empty(component.FindAll(".run-gantt-step-row"));

        component.FindAll("button.run-gantt-toggle")[1].Click();

        var toggle = component.FindAll("button.run-gantt-toggle")[1];
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
        var group = component.Find(".run-gantt-steps");
        Assert.Equal(group.GetAttribute("id"), toggle.GetAttribute("aria-controls"));
        Assert.Equal(3, component.FindAll(".run-gantt-step-row").Count);
        var subBars = component.FindAll(".run-gantt-step-bar");
        Assert.Equal(2, subBars.Count);
        Assert.Equal(["50", "75"], subBars.Select(bar => bar.GetAttribute("x")));
        Assert.Equal(["25", "25"], subBars.Select(bar => bar.GetAttribute("width")));
        Assert.Contains("run-gantt-failed", subBars[1].GetAttribute("class"), StringComparison.Ordinal);
        // The step that never started is listed with its status icon and no bar.
        Assert.Single(component.FindAll(".run-gantt-step-nostart"));
        Assert.Equal(3, component.FindAll(".run-gantt-step-row .run-gantt-status-icon").Count);
        // Columns stay aligned: every step row has as many cells as the header, Duration filled.
        var headerCells = component.FindAll(".run-gantt-head .run-gantt-cols > span").Count;
        Assert.All(component.FindAll(".run-gantt-step-row"), row =>
            Assert.Equal(headerCells, row.QuerySelectorAll(".run-gantt-col").Length));
        Assert.Equal("50s", component.FindAll(".run-gantt-step-row .run-gantt-col[data-column='Duration']")[0].TextContent);
        Assert.Contains("GanttTooltipDuration",
            component.FindAll(".run-gantt-step-track")[0].GetAttribute("title"), StringComparison.Ordinal);

        component.FindAll("button.run-gantt-toggle")[1].Click();

        Assert.Equal("false", component.FindAll("button.run-gantt-toggle")[1].GetAttribute("aria-expanded"));
        Assert.Empty(component.FindAll(".run-gantt-step-row"));
    }

    [Fact]
    public void AStepSkippedByItsCondition_IsNamedSkipped_WhileACancelledOneStaysCancelled()
    {
        // Both are stored Cancelled with no start; only the condition tells them apart.
        var skipped = Step(2, "Lint", null, 100, TaskExecutionStatus.Cancelled) with
        {
            SkippedCondition = "eq(variables['HAS_PYTHON'], 'true')"
        };
        var component = Render(
            Step(1, "Lint", 0, 100),
            skipped,
            Step(3, "Lint", null, 100, TaskExecutionStatus.Cancelled));

        component.Find("button.run-gantt-toggle").Click();

        var labels = component.FindAll(".run-gantt-step-row .run-gantt-status-icon")
            .Select(icon => icon.GetAttribute("aria-label"))
            .ToList();
        Assert.Equal(["Enum_TaskExecutionStatus_Success", "Skipped", "Enum_TaskExecutionStatus_Cancelled"], labels);
    }

    [Fact]
    public void TheBarTooltipGivesStartEndDurationWaitAndTheGapToTheUsualDuration()
    {
        // Stage duration tooltip.
        var clock = new FakeTimeProvider(Origin.AddSeconds(1000));
        var component = Render<RunGanttStrip>(parameters => parameters
            .Add(p => p.Run, new PipelineRunDto
            {
                Id = 1,
                Steps = [Step(1, "Compile", 0, 100, depth: 0), Step(2, "Test", 130, 200, depth: 1)]
            })
            .Add(p => p.Baselines, new RunStageBaselinesDto
            {
                Stages = [new StageBaselineDto { StageName = "Test", AverageSeconds = 60 }]
            })
            .Add(p => p.Clock, clock));

        var title = component.FindAll(".run-gantt-track")[1].GetAttribute("title")!;
        Assert.Contains("GanttTooltipStart", title, StringComparison.Ordinal);
        Assert.Contains("GanttTooltipEnd", title, StringComparison.Ordinal);
        Assert.Contains("GanttTooltipDuration", title, StringComparison.Ordinal);
        Assert.Contains("GanttTooltipWait", title, StringComparison.Ordinal);
        Assert.Contains("GanttTooltipDeviation", title, StringComparison.Ordinal);
    }
    [Fact]
    public void Front013_NowTakesTheKindOfTheRunTimestamps()
    {
        // The DTO timestamps arrive browser-local (UtcToLocalDateTimeConverter): a UTC "now" subtracted
        // from them measured the browser's time-zone offset as elapsed time.
        var clock = new FakeTimeProvider(Origin);
        var local = Origin.ToLocalTime();

        var now = RunGanttStrip.NowFor(clock, local);

        Assert.Equal(DateTimeKind.Local, now.Kind);
        Assert.Equal(TimeSpan.Zero, now - local);
        Assert.Equal(DateTimeKind.Utc, RunGanttStrip.NowFor(clock, Origin).Kind);
    }

    [Fact]
    public void Front013_ARunningRunDeserializedLikeTheApiAnswer_MeasuresItsRealDuration()
    {
        // Wire format: UTC. The client converter turns it browser-local, as in production.
        var run = System.Text.Json.JsonSerializer.Deserialize<PipelineRunDto>(System.Text.Json.JsonSerializer.Serialize(new PipelineRunDto
        {
            Id = 1,
            StartedAt = Origin,
            Status = PipelineStatus.Running,
            Steps = [Step(1, "Compile", 0, null, TaskExecutionStatus.Running)]
        }), Aetheus.Front.Components.Shared.JsonOptions.Web)!;
        Assert.Equal(DateTimeKind.Local, run.StartedAt.Kind);
        var clock = new FakeTimeProvider(Origin.AddMinutes(5));

        var component = Render<RunGanttStrip>(parameters => parameters.Add(p => p.Run, run).Add(p => p.Clock, clock));

        Assert.Equal(TimeSpan.FromMinutes(5), component.Instance.RunDuration());
        Assert.Equal(TimeSpan.FromMinutes(5), component.Instance.Layout!.Duration);
    }
}
