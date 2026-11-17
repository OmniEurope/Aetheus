// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.AiTasks;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Ai;

/// <summary>
/// Recette R-12 on the AI task list: a run started before the user left the page is still running when they come
/// back, and its row says so (busy Run button) instead of offering a second run. The live task feed links an AI run to
/// its definition by id, never by the definition's display name: a rename of the definition (or two definitions with
/// the same name) must neither lose the run nor hold the wrong row. A run of a pipeline, a task that is not an AI run
/// or one that ended is not a run in progress of a row.
/// </summary>
public sealed class AiTasksRunningOnArrivalTests : BunitContext
{
    private const int NightlyId = 7;
    private const int WeeklyId = 8;

    private readonly BunitTestHelper.TestHandler _handler;

    public AiTasksRunningOnArrivalTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetJsonResponse("api/ai/consumption", new AiConsumptionDto());
        _handler.SetJsonResponse("api/ai/profile-options", new List<AiRunnerProfileDto>());
        _handler.SetJsonResponse("api/ai/tasks", new PaginatedResult<AiTaskDefinitionDto>
        {
            Items =
            [
                new AiTaskDefinitionDto { Id = NightlyId, Name = "nightly review", ServerId = 20, ServerName = "runner" },
                new AiTaskDefinitionDto { Id = WeeklyId, Name = "weekly audit", ServerId = 20, ServerName = "runner" }
            ],
            TotalCount = 2
        });
    }

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    // An AI run as the feed carries it: named after its definition when it was queued, and linked to it by id.
    private static ServerTaskDto Run(int id, int definitionId, string name, int? pipelineRunId = null,
        TaskExecutionStatus status = TaskExecutionStatus.Running, OperationKind operation = OperationKind.AiRun) =>
        new()
        {
            Id = id,
            ServerId = 20,
            Name = name,
            Status = status,
            PipelineRunId = pipelineRunId,
            Operation = operation,
            AiTaskDefinitionId = definitionId
        };

    private IRenderedComponent<AiTasks> RenderList()
    {
        var cut = Render<AiTasks>();
        cut.WaitForState(() => cut.Markup.Contains("weekly audit", StringComparison.Ordinal));
        return cut;
    }

    // The Run button of the row: its title is the action's key in the test localizer.
    private static bool RunIsBusy(IRenderedComponent<AiTasks> cut, int row) =>
        cut.FindAll("button[title='RunNow']")[row].HasAttribute("disabled");

    [Fact]
    public void ARunStartedBeforeTheUserLeft_ShowsItsRowBusy_OnArrival()
    {
        Tracker.Upsert(Run(73001, NightlyId, "nightly review"));

        var cut = RenderList();

        Assert.True(RunIsBusy(cut, 0));
        // Only the row whose run is in progress is held: another task can still be started.
        Assert.False(RunIsBusy(cut, 1));
        Assert.Single(cut.FindAll("button[title='RunNow'].btn-busy"));
    }

    [Fact]
    public void ARunQueuedButNotYetStarted_ShowsItsRowBusy_OnArrival()
    {
        Tracker.Upsert(Run(73002, WeeklyId, "weekly audit", status: TaskExecutionStatus.Pending));

        var cut = RenderList();

        Assert.Single(cut.FindAll("button[title='RunNow'].btn-busy"));
        Assert.Contains("btn-busy", cut.FindAll("button[title='RunNow']")[1].ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunThatStartsWhileThePageIsOpen_ShowsItsRowBusy_ThenFreesItWhenItEnds()
    {
        var cut = RenderList();
        Assert.False(RunIsBusy(cut, 0));

        Tracker.Upsert(Run(73003, NightlyId, "nightly review"));
        cut.WaitForAssertion(() => Assert.True(RunIsBusy(cut, 0)));

        Tracker.HandleTerminal(new TaskCompletedNotification
        {
            TaskId = 73003,
            ServerId = 20,
            TaskName = "nightly review",
            Status = TaskExecutionStatus.Success,
            Operation = OperationKind.AiRun
        });
        cut.WaitForAssertion(() => Assert.False(RunIsBusy(cut, 0)));
    }

    [Fact]
    public void ARunQueuedUnderTheOldNameOfARenamedTask_StillHoldsItsRow()
    {
        // The run was queued while the definition was called "old nightly"; the list now reads the new name.
        Tracker.Upsert(Run(73008, NightlyId, "old nightly"));

        var cut = RenderList();

        Assert.True(RunIsBusy(cut, 0));
        Assert.False(RunIsBusy(cut, 1));
    }

    [Fact]
    public void ARunWithTheNameOfARowButTheIdOfAnotherDefinition_DoesNotHoldThatRow()
    {
        // Another definition (not listed here) happens to share the display name of the first row.
        Tracker.Upsert(Run(73009, definitionId: 999, name: "nightly review"));

        var cut = RenderList();

        Assert.False(RunIsBusy(cut, 0));
        Assert.False(RunIsBusy(cut, 1));
        Assert.Empty(cut.FindAll("button[title='RunNow'].btn-busy"));
    }

    [Fact]
    public void ATaskThatIsNotARunOfARow_LeavesTheRowsFree()
    {
        // A run of a pipeline step, a run that ended, a task of another kind named like the row, a run without a link.
        Tracker.Upsert(Run(73005, NightlyId, "nightly review", pipelineRunId: 5));
        Tracker.Upsert(Run(73006, NightlyId, "nightly review", status: TaskExecutionStatus.Success));
        Tracker.Upsert(Run(73007, NightlyId, "nightly review", operation: OperationKind.None));
        Tracker.Upsert(new ServerTaskDto { Id = 73010, ServerId = 20, Name = "nightly review", Status = TaskExecutionStatus.Running, Operation = OperationKind.AiRun });

        var cut = RenderList();

        Assert.False(RunIsBusy(cut, 0));
        Assert.False(RunIsBusy(cut, 1));
        Assert.Empty(cut.FindAll("button[title='RunNow'].btn-busy"));
    }
}
