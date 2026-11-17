// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Shared;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// Recette R-4 (ex R-12): the one matcher every page uses to find, in the live task feed, the task it must show again
/// when the user comes back. A task is adoptable when it belongs to the page's server, is queued or running, is not a
/// pipeline step (the run page reports those) and carries one of the names the page launches.
/// </summary>
public sealed class ServerTaskAdoptionTests
{
    private const int ServerId = 20;

    private static ServerTaskDto Task(int id, string name, TaskExecutionStatus status = TaskExecutionStatus.Running,
        int serverId = ServerId, int? pipelineRunId = null) =>
        new() { Id = id, ServerId = serverId, Name = name, Status = status, PipelineRunId = pipelineRunId };

    [Theory]
    [InlineData(TaskExecutionStatus.Pending, true)]
    [InlineData(TaskExecutionStatus.Assigned, true)]
    [InlineData(TaskExecutionStatus.Running, true)]
    [InlineData(TaskExecutionStatus.Success, false)]
    [InlineData(TaskExecutionStatus.Failed, false)]
    [InlineData(TaskExecutionStatus.Cancelled, false)]
    public void IsInFlight_IsTrueOnlyForQueuedAssignedAndRunning(TaskExecutionStatus status, bool expected) =>
        Assert.Equal(expected, ServerTaskAdoption.IsInFlight(Task(1, "x", status)));

    [Fact]
    public void FindInFlight_ByPrefix_FindsTheRunningTaskOfTheServer()
    {
        var tasks = new[] { Task(1, "Certbot RenewalCheck") };

        var found = ServerTaskAdoption.FindInFlight(tasks, ServerId, ["Certbot"]);

        Assert.Equal(1, found?.Id);
    }

    [Fact]
    public void FindInFlight_IgnoresTheCaseOfThePrefix() =>
        Assert.NotNull(ServerTaskAdoption.FindInFlight([Task(1, "certbot renewal")], ServerId, ["Certbot"]));

    [Fact]
    public void FindInFlight_SkipsATaskOfAnotherServer() =>
        Assert.Null(ServerTaskAdoption.FindInFlight([Task(1, "Certbot x", serverId: 21)], ServerId, ["Certbot"]));

    [Fact]
    public void FindInFlight_SkipsATaskThatEnded() =>
        Assert.Null(ServerTaskAdoption.FindInFlight([Task(1, "Certbot x", TaskExecutionStatus.Success)], ServerId, ["Certbot"]));

    [Fact]
    public void FindInFlight_SkipsAPipelineStep_UnlessAskedToKeepIt()
    {
        var tasks = new[] { Task(1, "Certbot x", pipelineRunId: 9) };

        Assert.Null(ServerTaskAdoption.FindInFlight(tasks, ServerId, ["Certbot"]));
        Assert.NotNull(ServerTaskAdoption.FindInFlight(tasks, ServerId, ["Certbot"], includePipelineTasks: true));
    }

    [Fact]
    public void FindInFlight_SkipsTheNotAdoptedPrefixes_ButKeepsTheOthersOfTheSamePage()
    {
        var tasks = new[] { Task(1, "Docker list"), Task(2, "Docker restart abc") };

        var found = ServerTaskAdoption.FindInFlight(tasks, ServerId, ["Docker"], notAdoptedPrefixes: ["Docker list"]);

        Assert.Equal(2, found?.Id);
    }

    [Fact]
    public void FindInFlight_WithNoPrefix_AdoptsNothing_ItWouldMatchEveryTaskOfTheServer()
    {
        var tasks = new[] { Task(1, "Anything") };

        Assert.Null(ServerTaskAdoption.FindInFlight(tasks, ServerId, []));
        Assert.Null(ServerTaskAdoption.FindInFlight(tasks, ServerId, [""]));
    }

    [Fact]
    public void FindInFlight_ByPredicate_MatchesTheExactNameTheCallerGives()
    {
        var tasks = new[] { Task(1, "Installed packages and more"), Task(2, "Installed packages") };

        var found = ServerTaskAdoption.FindInFlight(tasks, ServerId, name => name == "Installed packages");

        Assert.Equal(2, found?.Id);
    }
}
