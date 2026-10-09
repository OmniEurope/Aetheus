// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// Recette R2-104 and audit FRONT-010, on the run page: a step's log pane showed only the tail of the
/// log (113 lines in run 2551) while the live stream grew without bound.
/// </summary>
public sealed class PipelineRunCompleteLogTests : BunitContext
{
    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunCompleteLogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
    }

    [Fact]
    public async Task AStepWhoseLinesWereOnlyStreamed_ReadsItsHistoryWhenSelected()
    {
        _handler.SetJsonResponse("api/logs/task/300", new List<TaskLogDto>());
        var streamedOnly = Step(1, 301);
        var cut = RenderRun(PipelineStatus.Success, streamedOnly, Step(2, 300));
        _handler.SetJsonResponse("api/logs/task/301", Lines(301, 1, 120));
        // What the live stream left while the step ran: its last three lines, the history never read.
        await cut.InvokeAsync(() => Logs(cut)[301] = [.. Lines(301, 118, 3)]);

        await cut.InvokeAsync(() => cut.Instance.SelectStep(streamedOnly));

        Assert.Contains(_handler.Requests, request => request.Url.Contains("api/logs/task/301", StringComparison.Ordinal));
        Assert.Equal(Enumerable.Range(1, 120), Logs(cut)[301].Select(line => line.Id));
    }

    [Fact]
    public async Task TheLiveStream_KeepsABoundedWindowOfTheNewestLines()
    {
        var running = Step(1, 300) with { Status = TaskExecutionStatus.Running };
        _handler.SetJsonResponse("api/logs/task/300", new List<TaskLogDto>());
        var cut = RenderRun(PipelineStatus.Running, running);
        var streamer = typeof(PipelineRun).GetField("_logStreamer", Priv)!.GetValue(cut.Instance)!;
        var append = streamer.GetType().GetMethod("AppendStreamedLogsAsync", Priv)!;

        await cut.InvokeAsync(() => (Task)append.Invoke(streamer, [Lines(300, 1, PipelineRunLogWindow.LiveWindow + 100)])!);

        var held = Logs(cut)[300];
        Assert.Equal(PipelineRunLogWindow.LiveWindow, held.Count);
        Assert.Equal(PipelineRunLogWindow.LiveWindow + 100, held[^1].Id);
        Assert.Equal(101, held[0].Id);
    }

    [Fact]
    public void ShowCompleteLog_ReadsEveryPage_AndKeepsTheLinesStreamedMeanwhile()
    {
        const int total = 6000;
        // The tail the pane opens with: the last LiveWindow lines.
        _handler.SetJsonResponse("api/logs/task/300?maxLines", Lines(300, total - PipelineRunLogWindow.LiveWindow + 1, PipelineRunLogWindow.LiveWindow));
        for (var page = 1; page <= total / PipelineRunLogWindow.PageSize; page++)
        {
            _handler.SetJsonResponse($"api/logs/task/300/page?page={page}&", new PaginatedResult<TaskLogDto>
            {
                Items = Lines(300, (page - 1) * PipelineRunLogWindow.PageSize + 1, PipelineRunLogWindow.PageSize),
                TotalCount = total,
                Page = page,
                PageSize = PipelineRunLogWindow.PageSize
            });
        }
        var cut = RenderRun(PipelineStatus.Success, Step(1, 300));
        cut.WaitForAssertion(() => Assert.Contains("ShowCompleteLog", cut.Markup, StringComparison.Ordinal));
        // A line that arrives while the pages are read (a late batch of the step) is not lost.
        cut.InvokeAsync(() => Logs(cut)[300].Add(new TaskLogDto { Id = total + 1, TaskId = 300, Message = "late", Timestamp = Stamp(total + 1) }));

        cut.FindAll("button").Single(button => button.TextContent.Contains("ShowCompleteLog", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("ShowCompleteLog", cut.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(1, total + 1), Logs(cut)[300].Select(line => line.Id));
    }

    [Fact]
    public void ALineStreamedAndReadAgainInASnapshot_IsKeptOnce()
    {
        var window = new PipelineRunLogWindow();
        window.AppendStreamed(300, Lines(300, 9, 2));

        window.AppendStreamed(300, Lines(300, 10, 2));
        window.ApplySnapshot(300, Lines(300, 1, 10), PipelineRunLogWindow.LiveWindow);

        Assert.Equal(Enumerable.Range(1, 11), window.Lines[300].Select(line => line.Id));
        Assert.False(window.HasEarlierLines(300));
    }

    private IRenderedComponent<PipelineRun> RenderRun(PipelineStatus status, params PipelineStepRunDto[] steps)
    {
        _handler.SetJsonResponse("api/pipelines/runs/42", new PipelineRunDto
        {
            Id = 42,
            PipelineId = 7,
            PipelineName = "CI Build",
            Status = status,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = status == PipelineStatus.Running ? null : DateTime.UtcNow,
            Steps = [.. steps],
            ResolvedVariables = [],
            Warnings = []
        });
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo("/pipelines/runs/42?tab=logs");
        var cut = Render<PipelineRun>(p => p.Add(x => x.RunId, 42));
        cut.WaitForState(() => cut.Instance.SelectedStep?.TaskId is { } taskId && cut.Instance.LogWindow.HasSnapshot(taskId),
            TimeSpan.FromSeconds(5));
        return cut;
    }

    private static Dictionary<int, List<TaskLogDto>> Logs(IRenderedComponent<PipelineRun> cut) => cut.Instance.LogWindow.Lines;

    private static PipelineStepRunDto Step(int id, int taskId) => new()
    {
        Id = id,
        StepName = $"step-{id}",
        StageName = "Build",
        Status = TaskExecutionStatus.Success,
        TaskId = taskId
    };

    private static List<TaskLogDto> Lines(int taskId, int firstId, int count) =>
        [.. Enumerable.Range(firstId, count).Select(id => new TaskLogDto
        {
            Id = id,
            TaskId = taskId,
            Message = $"line {id}",
            Timestamp = Stamp(id)
        })];

    private static DateTime Stamp(int id) => new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc).AddMilliseconds(id);
}
