// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>Recette R2-128: the "latest grade" and "last success" tiles open the run they describe.</summary>
public class PipelineRunsSummaryTests
{
    private static readonly DateTime Start = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_NamesTheLatestGradedRun_AndTheLastSuccessfulRun()
    {
        PipelineRunDto[] runs =
        [
            Run(40, PipelineStatus.Running, minutes: 40, grade: null, completed: false),
            Run(30, PipelineStatus.Failed, minutes: 30, grade: AnalysisGrade.F),
            Run(20, PipelineStatus.Success, minutes: 20, grade: AnalysisGrade.A),
            Run(10, PipelineStatus.Success, minutes: 10, grade: AnalysisGrade.B)
        ];

        var summary = PipelineRunsSummary.Create(runs);

        Assert.Equal(AnalysisGrade.F, summary.LatestGrade);
        Assert.Equal(30, summary.LatestGradeRunId);
        Assert.Equal(20, summary.LastSuccessRunId);
        Assert.Equal(runs[2].CompletedAt, summary.LastSuccessAt);
    }

    [Fact]
    public void Create_WithoutGradeOrSuccess_NamesNoRun()
    {
        var summary = PipelineRunsSummary.Create([Run(5, PipelineStatus.Failed, minutes: 5, grade: null)]);

        Assert.Null(summary.LatestGradeRunId);
        Assert.Null(summary.LastSuccessRunId);
    }

    [Fact]
    public void GateHref_OpensTheRunOnItsGateTab()
    {
        Assert.Equal("/pipelines/runs/7?projectId=1&tab=gate", PipelineRunPresentation.GateHref(7, 1, null));
        Assert.Equal("/pipelines/runs/7?serverId=3&tab=gate", PipelineRunPresentation.GateHref(7, null, 3));
        Assert.Equal("/pipelines/runs/7?tab=gate", PipelineRunPresentation.GateHref(7, null, null));
    }

    private static PipelineRunDto Run(int id, PipelineStatus status, int minutes, AnalysisGrade? grade, bool completed = true) => new()
    {
        Id = id,
        Status = status,
        StartedAt = Start.AddMinutes(minutes),
        CompletedAt = completed ? Start.AddMinutes(minutes + 2) : null,
        GateGrade = grade
    };
}
