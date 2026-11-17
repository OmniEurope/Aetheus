// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// R-10 on the runs of a pipeline: a run launched or cancelled from the page shows at once from the action's answer, before
/// the runs are read again; the read runs in the background and replaces the rows, and a refused action (a 409 included)
/// leaves the rows and shows an error toast without reading the list again.
/// </summary>
public sealed class PipelineEditRowChangesTests : BunitContext
{
    private const string RunButton = ".omni-page-header__pinned button.omni-button";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public PipelineEditRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<YamlSerializationService>();
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse("api/ai/profile-options", new List<AiRunnerProfileDto>());
        _handler.SetJsonResponse("api/pipelines/5", new PipelineDto
        {
            Id = 5,
            Name = "Deploy Prod",
            Description = "Production deploy",
            YamlDefinition = "name: deploy\ntrigger: manual\nstages: []",
            TriggerType = PipelineTriggerType.Manual
        });
        _handler.SetJsonResponse("api/pipelines/5/runs", Runs(Run(7, PipelineStatus.Running), Run(3, PipelineStatus.Success)));
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/pipelines/templates", new List<PipelineTemplateSummaryDto>());
        _handler.SetJsonResponse("api/pipelines/5/preflight", new PipelinePreflightDto());
        _handler.SetJsonResponse("api/pipelines/5/parameters", new List<PipelineRunParameterDto>());
    }

    private static PipelineRunDto Run(int id, PipelineStatus status) =>
        new() { Id = id, BuildNumber = id, PipelineId = 5, PipelineName = "Deploy Prod", Status = status, StartedAt = DateTime.Now.AddMinutes(-id) };

    private static PaginatedResult<PipelineRunDto> Runs(params PipelineRunDto[] runs) =>
        new() { Items = [.. runs], TotalCount = runs.Length, Page = 1, PageSize = 25 };

    private TaskCompletionSource<PaginatedResult<PipelineRunDto>> HoldTheNextRunsRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<PipelineRunDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/pipelines/5/runs", _ => gate.Task);
        return gate;
    }

    private int RunsReads() => _handler.RequestsSnapshot()
        .Count(request => request.Method == "GET" && request.Url.Contains("api/pipelines/5/runs?", StringComparison.Ordinal));

    private IRenderedComponent<PipelineEdit> RenderPipeline()
    {
        var cut = Render<PipelineEdit>(parameters => parameters.Add(page => page.Id, 5));
        cut.WaitForAssertion(() => Assert.True(RowHolds(cut, "#7")));
        return cut;
    }

    private static bool RowHolds(IRenderedComponent<PipelineEdit> cut, string text) =>
        cut.FindAll("tbody tr").Any(row => row.TextContent.Contains(text, StringComparison.Ordinal));

    private static string RowText(IRenderedComponent<PipelineEdit> cut, string run) =>
        cut.FindAll("tbody tr").First(row => row.TextContent.Contains(run, StringComparison.Ordinal)).TextContent;

    private static Task CancelAsync(IRenderedComponent<PipelineEdit> cut) =>
        cut.FindAll("tbody button").Single(button => button.GetAttribute("title") == "CancelRun").ClickAsync(new MouseEventArgs());

    private void AssertDanger() => Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger);

    // ---------- launch ----------

    [Fact]
    public async Task ALaunchedRun_ShowsTheAnsweredRowAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderPipeline();
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/5/run", Run(9, PipelineStatus.Pending));
        var gate = HoldTheNextRunsRead();
        var readsBefore = RunsReads();

        var action = cut.FindAll(RunButton)[0].ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Assert.True(RowHolds(cut, "#9")));
        cut.WaitForAssertion(() => Assert.Equal(readsBefore + 1, RunsReads()));
        Assert.True(RowHolds(cut, "#7"));

        gate.SetResult(Runs(Run(10, PipelineStatus.Pending), Run(9, PipelineStatus.Running), Run(7, PipelineStatus.Running), Run(3, PipelineStatus.Success)));
        cut.WaitForAssertion(() => Assert.True(RowHolds(cut, "#10")));
        Assert.True(RowHolds(cut, "#9"));
        await action;
    }

    [Fact]
    public async Task ALaunchedRun_NamesTheRunInASuccessNotice_WithAnActionToOpenIt()
    {
        var cut = RenderPipeline();
        _handler.SetJsonResponse(HttpMethod.Post, "api/pipelines/5/run", Run(9, PipelineStatus.Pending));
        // The read behind the launch holds the new run, as the server does.
        _handler.SetJsonResponse("api/pipelines/5/runs", Runs(Run(9, PipelineStatus.Pending), Run(7, PipelineStatus.Running), Run(3, PipelineStatus.Success)));

        await cut.FindAll(RunButton)[0].ClickAsync(new MouseEventArgs());

        var notice = Assert.Single(Services.GetRequiredService<OmniOverlayService>().Notifications,
            notification => notification.Severity == OmniSeverity.Success);
        Assert.Contains("PipelineTriggered", notice.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(notice.ActionText));
        cut.WaitForAssertion(() => Assert.True(RowHolds(cut, "#9")));
    }

    [Fact]
    public async Task ASecondClickWhileTheFirstLaunchIsInFlight_StartsNoSecondRun()
    {
        var cut = RenderPipeline();
        var answer = new TaskCompletionSource<PipelineRunDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/pipelines/5/run", _ => answer.Task);
        _handler.SetJsonResponse("api/pipelines/5/runs", Runs(Run(9, PipelineStatus.Pending), Run(7, PipelineStatus.Running), Run(3, PipelineStatus.Success)));

        var first = cut.FindAll(RunButton)[0].ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Assert.Equal(1, RunPosts()));
        await cut.FindAll(RunButton)[0].ClickAsync(new MouseEventArgs());
        Assert.Equal(1, RunPosts());
        // Still in flight: the "launching" notice is on screen (it does not expire after the usual few seconds).
        Assert.Single(Services.Toasts(), toast => toast.Detail == "PipelineLaunching");

        answer.SetResult(Run(9, PipelineStatus.Pending));
        await first;
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Detail == "PipelineLaunching");
        cut.WaitForAssertion(() => Assert.True(RowHolds(cut, "#9")));
        Assert.Equal(1, RunPosts());
    }

    private int RunPosts() => _handler.RequestsSnapshot()
        .Count(request => request.Method == "POST"
            && System.Text.RegularExpressions.Regex.IsMatch(request.Url, @"api/pipelines/5/run(\?|$)"));

    [Fact]
    public async Task ARefusedLaunch_LeavesTheRows_ShowsTheError_AndReadsNothingAgain()
    {
        var cut = RenderPipeline();
        _handler.SetResponse(HttpMethod.Post, "api/pipelines/5/run", HttpStatusCode.Conflict);
        var readsBefore = RunsReads();

        await cut.FindAll(RunButton)[0].ClickAsync(new MouseEventArgs());

        AssertDanger();
        Assert.True(RowHolds(cut, "#7"));
        Assert.Equal(readsBefore, RunsReads());
    }

    // ---------- cancel ----------

    [Fact]
    public async Task ACancelledRun_ShowsItsNewStatusAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderPipeline();
        _dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Post, "api/pipelines/runs/7/cancel", HttpStatusCode.NoContent);
        var gate = HoldTheNextRunsRead();
        var readsBefore = RunsReads();

        var action = CancelAsync(cut);

        cut.WaitForAssertion(() => Assert.DoesNotContain(cut.FindAll("tbody button"), button => button.GetAttribute("title") == "CancelRun"));
        cut.WaitForAssertion(() => Assert.Equal(readsBefore + 1, RunsReads()));
        Assert.True(RowHolds(cut, "#3"));

        gate.SetResult(Runs(Run(11, PipelineStatus.Failed), Run(7, PipelineStatus.Cancelled), Run(3, PipelineStatus.Success)));
        cut.WaitForAssertion(() => Assert.True(RowHolds(cut, "#11")));
        await action;
    }

    /// <summary>The page showed a run as running while it had finished: the server answers 404. The reader gets a
    /// sentence that says so (a warning, not a failure report) and the row is read again from the current state.</summary>
    [Fact]
    public async Task ACancelOfARunAlreadyFinished_SaysSo_AndReadsTheRunsAgain()
    {
        var cut = RenderPipeline();
        _dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Post, "api/pipelines/runs/7/cancel", HttpStatusCode.NotFound);
        _handler.SetJsonResponse("api/pipelines/5/runs", Runs(Run(7, PipelineStatus.Success), Run(3, PipelineStatus.Success)));
        var readsBefore = RunsReads();

        await CancelAsync(cut);

        var toast = Assert.Single(Services.Toasts());
        Assert.Equal(OmniSeverity.Warning, toast.Severity);
        Assert.Equal("RunNoLongerCancellable", toast.Detail);
        cut.WaitForAssertion(() => Assert.Equal(readsBefore + 1, RunsReads()));
        cut.WaitForAssertion(() => Assert.DoesNotContain(cut.FindAll("tbody button"), button => button.GetAttribute("title") == "CancelRun"));
    }

    [Fact]
    public async Task ARefusedCancel_KeepsTheRun_ShowsTheError_AndReadsNothingAgain()
    {
        var cut = RenderPipeline();
        _dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Post, "api/pipelines/runs/7/cancel", HttpStatusCode.Conflict);
        var readsBefore = RunsReads();

        await CancelAsync(cut);

        AssertDanger();
        Assert.Single(cut.FindAll("tbody button"), button => button.GetAttribute("title") == "CancelRun");
        Assert.Contains("#7", RowText(cut, "#7"), StringComparison.Ordinal);
        Assert.Equal(readsBefore, RunsReads());
    }
}
