// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using Aetheus.Front.Components.AiTasks;
using Aetheus.Front.Tests.TestDoubles;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Ai;

/// <summary>
/// The AI task list's row actions, all of which open a dialog and were therefore unreachable:
/// create/edit, run-now, results and delete. The delete confirmation is the one that matters, and
/// the run path is asserted on what it sends, not on the dialog it happens to open afterwards.
/// </summary>
public sealed class AiTasksActionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public AiTasksActionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse("api/ai/consumption", new AiConsumptionDto());
        _handler.SetJsonResponse("api/ai/profile-options", new List<AiRunnerProfileDto>());
        _handler.SetJsonResponse("api/ai/tasks", new PaginatedResult<AiTaskDefinitionDto>
        {
            Items = [new AiTaskDefinitionDto { Id = 7, Name = "nightly review", ServerId = 20, ServerName = "runner" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/ai/results", new PaginatedResult<AiRunResultDto>
        {
            Items = [],
            TotalCount = 0
        });
    }

    private IRenderedComponent<AiTasks> RenderList()
    {
        var cut = Render<AiTasks>();
        cut.WaitForState(
            () => cut.Markup.Contains("nightly review", StringComparison.Ordinal));
        return cut;
    }

    // R1-4: Run now is the row's visible action, the others are entries of its "More actions" menu.
    private static IElement Action(IRenderedComponent<AiTasks> cut, string title) =>
        cut.FindAll("button").FirstOrDefault(button =>
            string.Equals(button.GetAttribute("title"), title, StringComparison.Ordinal))
        ?? cut.FindMenuItem(title)
        ?? throw new InvalidOperationException($"No button or menu entry named {title}");

    private bool Sent(string method, string urlContains) =>
        _handler.Requests.Any(request => request.Method == method
            && request.Url.Contains(urlContains, StringComparison.Ordinal));

    /// <summary>R1-4: a row keeps one visible action (Run now); Results, Edit and Delete are in its menu.</summary>
    [Fact]
    public void ARowShowsRunAsItsOnlyAction_AndKeepsTheOthersInTheMenu()
    {
        var cut = RenderList();

        Assert.Single(cut.FindAll("button[title='RunNow']"));
        Assert.Empty(cut.FindAll("button[title='Results'], button[title='Edit'], button[title='Delete']"));
        Assert.Single(cut.FindAll(".omni-overflow-menu__trigger"));
        foreach (var label in new[] { "Results", "Edit", "Delete" })
            Assert.NotNull(cut.FindMenuItem(label));
    }

    // ---------- create and edit ----------

    [Fact]
    public void CreatingOpensTheTaskDialogWithNoDefinitionAndNoFixedProject()
    {
        var cut = RenderList();

        cut.FindAll("button").First(button => button.TextContent.Contains("Create", StringComparison.Ordinal)).Click();

        Assert.Equal(typeof(AiTaskDialog), _dialog.LastComponent);
        Assert.Null(Assert.Contains("DefinitionId", _dialog.LastParameters!));
        Assert.Null(Assert.Contains("FixedProjectId", _dialog.LastParameters!));
    }

    [Fact]
    public void EditingOpensTheTaskDialogOnThatDefinition()
    {
        var cut = RenderList();

        Action(cut, "Edit").Click();

        Assert.Equal(typeof(AiTaskDialog), _dialog.LastComponent);
        Assert.Equal(7, Assert.Contains("DefinitionId", _dialog.LastParameters!));
    }

    [Fact]
    public void CreatingInsideAProjectPinsThatProjectOnTheDialog()
    {
        _handler.SetJsonResponse("api/projects/10", new ProjectDetailDto { Id = 10, Name = "aetheus" });
        var cut = Render<AiTasks>(parameters => parameters.Add(page => page.ProjectId, 10));
        cut.WaitForState(
            () => cut.Markup.Contains("nightly review", StringComparison.Ordinal));

        cut.FindAll("button").First(button => button.TextContent.Contains("Create", StringComparison.Ordinal)).Click();

        Assert.Equal(10, Assert.Contains("FixedProjectId", _dialog.LastParameters!));
    }

    [Fact]
    public void ClosingTheDialogWithoutSavingDoesNotReloadTheList()
    {
        _dialog.OpenResult = false;
        var cut = RenderList();
        var before = _handler.Requests.Count(request =>
            request.Url.Contains("api/ai/tasks", StringComparison.Ordinal));

        Action(cut, "Edit").Click();

        Assert.Equal(
            before,
            _handler.Requests.Count(request => request.Url.Contains("api/ai/tasks", StringComparison.Ordinal)));
    }

    [Fact]
    public void SavingFromTheDialogReloadsTheList()
    {
        _dialog.OpenResult = true;
        var cut = RenderList();
        var before = _handler.Requests.Count(request =>
            request.Url.Contains("api/ai/tasks", StringComparison.Ordinal));

        Action(cut, "Edit").Click();

        cut.WaitForAssertion(
            () => Assert.True(_handler.Requests.Count(request =>
                request.Url.Contains("api/ai/tasks", StringComparison.Ordinal)) > before));
    }

    // ---------- results ----------

    [Fact]
    public void OpeningTheResults_GoesToTheTaskResultsPage()
    {
        var cut = RenderList();

        Action(cut, "Results").Click();

        Assert.EndsWith("/ai-tasks/7", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        Assert.Null(_dialog.LastComponent);
    }

    // ---------- run now ----------

    [Fact]
    public void RunningATaskPostsToItsRunEndpoint()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/ai/tasks/7/run", new AiRunStartDto { TaskId = 42 });
        var cut = RenderList();

        Action(cut, "RunNow").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/tasks/7/run")));
    }

    [Fact]
    public void RunningATaskThatAlreadyProducedAResultOpensItImmediately()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/ai/tasks/7/run", new AiRunStartDto { TaskId = 42 });
        _handler.SetJsonResponse("api/ai/results", new PaginatedResult<AiRunResultDto>
        {
            Items =
            [
                new AiRunResultDto
                {
                    Id = 100,
                    ServerTaskId = 42,
                    ProfileName = "claude",
                    ReportMarkdown = "all good",
                    CreatedAt = DateTime.UtcNow
                }
            ],
            TotalCount = 1
        });
        var cut = RenderList();

        Action(cut, "RunNow").Click();

        // Recette R3-17: the finished run opens on the task's results page, that run selected.
        var navigation = Services.GetRequiredService<NavigationManager>();
        cut.WaitForAssertion(
            () => Assert.EndsWith("/ai-tasks/7?result=100", navigation.Uri, StringComparison.Ordinal));
    }

    [Fact]
    public void ARefusedRunNeverOpensAResultDialog()
    {
        _handler.SetResponse(HttpMethod.Post, "api/ai/tasks/7/run", HttpStatusCode.Conflict);
        var cut = RenderList();

        Action(cut, "RunNow").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/tasks/7/run")));
        Assert.Equal(0, _dialog.OpenCount);
    }

    // ---------- delete ----------

    [Fact]
    public void DeletingIsGatedByAConfirmation()
    {
        _dialog.ConfirmResult = false;
        var cut = RenderList();

        Action(cut, "Delete").Click();

        Assert.False(Sent("DELETE", "api/ai/tasks/7"));
    }

    [Fact]
    public void ConfirmingTheDeleteCallsTheEndpointForThatTask()
    {
        _handler.SetResponse(HttpMethod.Delete, "api/ai/tasks/7", HttpStatusCode.NoContent);
        _dialog.ConfirmResult = true;
        var cut = RenderList();

        Action(cut, "Delete").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("DELETE", "api/ai/tasks/7")));
    }

    [Fact]
    public void ARefusedDeleteLeavesTheRowInPlace()
    {
        _handler.SetResponse(HttpMethod.Delete, "api/ai/tasks/7", HttpStatusCode.Conflict);
        _dialog.ConfirmResult = true;
        var cut = RenderList();

        Action(cut, "Delete").Click();

        cut.WaitForAssertion(() => Assert.True(Sent("DELETE", "api/ai/tasks/7")));
        Assert.Contains("nightly review", cut.Markup);
    }

    // ---------- realtime ----------

    [Fact]
    public void OnlyAnAiRunCompletionTriggersAReload()
    {
        Assert.True(AiTasks.ShouldReloadFor(new TaskCompletedNotification { Operation = OperationKind.AiRun }));
        Assert.False(AiTasks.ShouldReloadFor(new TaskCompletedNotification { Operation = OperationKind.BackupExecute }));
    }
}
