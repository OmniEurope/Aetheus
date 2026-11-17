// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R-12, Docker page, shell panel: coming back while a command runs in a container reopens that container's
/// shell panel with the output produced so far, and the end of the command completes it. The task name holds only
/// the first twelve characters of the container id, so it is matched against the loaded containers.
/// </summary>
public sealed class ServerDockerExecAdoptionTests : BunitContext
{
    private const int ServerId = 20;
    private const string ExecTaskName = "Docker exec - abc123def456";

    private readonly BunitTestHelper.TestHandler _handler;

    public ServerDockerExecAdoptionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    private static ServerTaskDto InFlight(int id, string name, TaskExecutionStatus status = TaskExecutionStatus.Running) =>
        new() { Id = id, ServerId = ServerId, Name = name, Status = status };

    private IRenderedComponent<ServerDockerSection> RenderSection()
    {
        foreach (var path in new[] { "containers", "images" })
            _handler.SetJsonResponse($"api/servers/{ServerId}/docker/{path}", "[]");
        var server = new ServerDetailDto
        {
            Id = ServerId,
            Name = "docker-srv",
            Hostname = "10.0.0.20",
            Type = ServerType.Docker,
            Status = ServerStatus.Online,
            Tags = [],
            Services = [],
            Docker = new DockerDataDto
            {
                Containers =
                [
                    new DockerContainerDto { ContainerId = "abc123def456aaaaaaaa", Name = "nginx", Image = "nginx:latest", State = "running", Status = "Up 2h" },
                    new DockerContainerDto { ContainerId = "fff000fff000bbbbbbbb", Name = "api", Image = "dotnet:8", State = "running", Status = "Up 2h" }
                ]
            }
        };
        return Render<ServerDockerSection>(p => p
            .Add(x => x.Server, server)
            .Add(x => x.ServerId, ServerId)
            .Add(x => x.DockerInitialLoaded, true));
    }

    private void Log(int taskId, params string[] lines) =>
        _handler.SetJsonResponse(HttpMethod.Get, $"api/logs/task/{taskId}",
            lines.Select((line, index) => new TaskLogDto { Id = index + 1, TaskId = taskId, Message = line }).ToList());

    [Fact]
    public void R12_ComingBackWhileAnExecRuns_ReopensTheShellOfThatContainerWithTheOutputSoFar()
    {
        Log(81001, "first line", "second line");
        Tracker.Upsert(InFlight(81001, ExecTaskName));

        var cut = RenderSection();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("DockerShell - nginx", cut.Markup, StringComparison.Ordinal);
            var output = cut.Find(".docker-shell-output").TextContent;
            Assert.Contains("first line", output, StringComparison.Ordinal);
            Assert.Contains("second line", output, StringComparison.Ordinal);
        });
        Assert.DoesNotContain("DockerShell - api", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R12_TheEndOfTheAdoptedExec_ShowsItsFullOutputOnceInThePanel()
    {
        Log(81002, "first line");
        Tracker.Upsert(InFlight(81002, ExecTaskName));
        var cut = RenderSection();
        cut.WaitForAssertion(() => Assert.Contains("first line", cut.Find(".docker-shell-output").TextContent, StringComparison.Ordinal));

        Log(81002, "first line", "last line");
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification
        {
            TaskId = 81002,
            ServerId = ServerId,
            TaskName = ExecTaskName,
            Status = TaskExecutionStatus.Success
        }));

        cut.WaitForAssertion(() =>
        {
            var output = cut.Find(".docker-shell-output").TextContent;
            Assert.Contains("last line", output, StringComparison.Ordinal);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(output, "first line"));
        });
    }

    [Theory]
    [InlineData("Docker exec - 000000000000", TaskExecutionStatus.Running)]
    [InlineData(ExecTaskName, TaskExecutionStatus.Success)]
    [InlineData("Docker logs - abc123def456", TaskExecutionStatus.Running)]
    public async Task R12_NoMatchingRunningExec_OpensNoShell(string name, TaskExecutionStatus status)
    {
        Log(81003, "never shown");
        Tracker.Upsert(InFlight(81003, name, status));

        var cut = RenderSection();
        await cut.InvokeAsync(() => { });

        Assert.DoesNotContain("DockerShell - ", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("never shown", cut.Markup, StringComparison.Ordinal);
    }
}
