// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// "Copy" in a run's log toolbar puts the opened step's log lines on the clipboard, one per line, and
/// confirms; a step whose log holds no line copies nothing.
/// </summary>
public sealed class PipelineRunCopyLogsTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunCopyLogsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse(HttpMethod.Get, "api/releases/by-run/", new List<ReleaseDto>());
        _handler.SetResponse(HttpMethod.Get, "/source", System.Net.HttpStatusCode.NoContent);
        _handler.SetPaginatedJsonResponse<PipelineRunDto>(HttpMethod.Get, "/runs?pageSize=50", []);
        _handler.SetPaginatedJsonResponse<AiRunResultDto>(HttpMethod.Get, "api/ai/results", []);
        _handler.SetResponse(HttpMethod.Get, "api/analysis/runs/", System.Net.HttpStatusCode.NoContent);
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/runs/95", new PipelineRunDto
        {
            Id = 95,
            PipelineId = 5,
            PipelineName = "aetheus-candidate",
            Status = PipelineStatus.Success,
            Steps =
            [
                new PipelineStepRunDto { Id = 21, StepName = "build", StageName = "Build", Status = TaskExecutionStatus.Success, TaskId = 700 }
            ]
        });
    }

    private IRenderedComponent<PipelineRun> RenderOnTheStep()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/pipelines/runs/95?tab=logs&step=21");
        var cut = Render<PipelineRun>(parameters => parameters.Add(component => component.RunId, 95));
        cut.WaitForAssertion(() => Assert.Equal(21, cut.Instance.SelectedStep?.Id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[title='CopyLogs']")));
        return cut;
    }

    private IReadOnlyList<object?> Copies() =>
        [.. JSInterop.Invocations
            .Where(invocation => invocation.Identifier == "Aetheus.copyToClipboard")
            .Select(invocation => invocation.Arguments[0])];

    [Fact]
    public async Task TheOpenedStepsLines_AreCopiedOnePerLine_AndConfirmed()
    {
        _handler.SetJsonResponse("api/logs/task/700", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 700, Message = "restore" },
            new() { Id = 2, TaskId = 700, Message = "build ok" }
        });
        var cut = RenderOnTheStep();
        cut.WaitForAssertion(() => Assert.Contains("build ok", cut.Markup, StringComparison.Ordinal));

        await cut.Find("button[title='CopyLogs']").ClickAsync(new());

        Assert.Equal(["restore\nbuild ok"], Copies());
        Assert.Contains(Services.Toasts(), toast => toast.Summary == "LogsCopied" || toast.Detail == "LogsCopied");
    }

    [Fact]
    public async Task AStepWithoutLines_CopiesNothing()
    {
        _handler.SetJsonResponse("api/logs/task/700", new List<TaskLogDto>());
        var cut = RenderOnTheStep();

        await cut.Find("button[title='CopyLogs']").ClickAsync(new());

        Assert.Empty(Copies());
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Summary == "LogsCopied" || toast.Detail == "LogsCopied");
    }
}
