// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-44: the small info a click on a block of the planner's calendar opens: the link to the pipeline, its
/// average duration, its latest run with its state and duration, and the gate and grade of that run.</summary>
public sealed class PipelineScheduleBlockInfoTests : BunitContext
{
    public PipelineScheduleBlockInfoTests()
    {
        BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
    }

    private static ScheduledTaskDto Task(ScheduleLastRunDto? lastRun, bool planned = false) => new()
    {
        PipelineId = 11,
        PipelineName = "aetheus-nightly",
        Enabled = true,
        DurationMinutes = 42,
        IsPlanned = planned,
        LastRun = lastRun
    };

    private IRenderedComponent<PipelineScheduleBlockInfo> Render(ScheduledTaskDto task) =>
        Render<PipelineScheduleBlockInfo>(parameters => parameters.Add(info => info.Task, task));

    [Fact]
    public void TheInfoLinksThePipeline_AndSaysItsAverageDuration()
    {
        var cut = Render(Task(null));

        Assert.Equal("pipelines/11", cut.Find("[data-testid='schedule-info-pipeline']").GetAttribute("href"));
        Assert.Equal("Average duration: 42 min", cut.Find("[data-testid='schedule-info-duration']").TextContent.Trim());
    }

    [Fact]
    public void ANeverMeasuredPipeline_SaysItsDurationIsPlanned()
    {
        var cut = Render(Task(null, planned: true));

        Assert.Equal("Planned duration: 42 min", cut.Find("[data-testid='schedule-info-duration']").TextContent.Trim());
    }

    [Fact]
    public void WithoutALastRun_TheInfoSaysItNeverRan_AndShowsNoRunNorGate()
    {
        var cut = Render(Task(null));

        Assert.Equal("Never ran", cut.Find("[data-testid='schedule-info-never']").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='schedule-info-run']"));
        Assert.Empty(cut.FindAll("[data-testid='schedule-info-gate']"));
    }

    [Fact]
    public void TheLastRun_IsLinked_WithItsStateAndItsDuration()
    {
        var run = new ScheduleLastRunDto { RunId = 77, BuildNumber = 12, Status = PipelineStatus.Success, DurationMinutes = 38 };

        var cut = Render(Task(run));

        var link = cut.Find("[data-testid='schedule-info-run-link']");
        Assert.Equal("/pipelines/runs/77", link.GetAttribute("href"));
        Assert.Equal("#12", link.TextContent.Trim());
        Assert.Contains("Success", cut.Find("[data-testid='schedule-info-run']").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("38 min", cut.Find("[data-testid='schedule-info-run-duration']").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='schedule-info-gate']"));
    }

    [Fact]
    public void ARunStillInProgress_HasNoDuration()
    {
        var run = new ScheduleLastRunDto { RunId = 78, BuildNumber = 13, Status = PipelineStatus.Running, DurationMinutes = null };

        var cut = Render(Task(run));

        Assert.Empty(cut.FindAll("[data-testid='schedule-info-run-duration']"));
    }

    [Fact]
    public void TheGateAndTheGradeOfTheLastRun_AreShownWhenThereIsAnAnalysis()
    {
        var run = new ScheduleLastRunDto
        {
            RunId = 77,
            BuildNumber = 12,
            Status = PipelineStatus.Failed,
            DurationMinutes = 5,
            GateStatus = AnalysisGateStatus.Blocked,
            Grade = AnalysisGrade.C
        };

        var cut = Render(Task(run));

        var gate = cut.Find("[data-testid='schedule-info-gate']");
        Assert.Contains("Blocked", gate.TextContent, StringComparison.Ordinal);
        Assert.Contains("C", gate.QuerySelector(".grade-badge")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AGateWithoutAGrade_ShowsTheGateOnly()
    {
        var run = new ScheduleLastRunDto { RunId = 77, BuildNumber = 12, Status = PipelineStatus.Success, GateStatus = AnalysisGateStatus.Passed };

        var cut = Render(Task(run));

        Assert.Contains("Passed", cut.Find("[data-testid='schedule-info-gate']").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid='schedule-info-gate'] .grade-badge"));
    }
}
