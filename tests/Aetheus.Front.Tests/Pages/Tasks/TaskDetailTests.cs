// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Tasks;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Tasks;

public class TaskDetailTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public TaskDetailTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Fact]
    public void RendersFailureDetailsAndLogs()
    {
        _handler.SetJsonResponse("api/tasks/7085", new ServerTaskDto
        {
            Id = 7085,
            ServerId = 9,
            ServerName = "vps2577917",
            Name = "Agent self-update to 1.0.1294",
            Command = "1.0.1294",
            Executor = ExecutorType.Operation,
            Status = TaskExecutionStatus.Failed,
            CreatedAt = new DateTime(2026, 8, 5, 7, 40, 7, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 8, 5, 7, 40, 8, DateTimeKind.Utc),
            ExitCode = -1,
            FailureCode = TaskFailureCodes.InfrastructureMismatch,
            FailureReason = "Linux integration posture upgrade supervisor is missing or obsolete.",
            TimeoutSeconds = 900
        });
        _handler.SetJsonResponse("api/logs/task/7085", new List<TaskLogDto>
        {
            new()
            {
                Id = 1,
                TaskId = 7085,
                Level = TaskLogLevel.Error,
                Message = "Self-update refused: the installed Linux integration posture does not support autonomous full upgrades.",
                Timestamp = new DateTime(2026, 8, 5, 7, 40, 8, DateTimeKind.Utc)
            }
        });

        var cut = Render<TaskDetail>(parameters => parameters.Add(p => p.TaskId, 7085));
        cut.WaitForState(() => cut.Markup.Contains("Agent self-update to 1.0.1294"));

        Assert.Contains("Task #7085", cut.Markup);
        Assert.Contains("InfrastructureMismatch", cut.Markup);
        Assert.Contains("Linux integration posture upgrade supervisor is missing or obsolete.", cut.Markup);
        Assert.Contains("Self-update refused", cut.Markup);
        Assert.Contains("Operation", cut.Markup);
        Assert.DoesNotContain("Enum_ExecutorType_Operation", cut.Markup);
        Assert.Contains("href=\"/servers/9/overview\"", cut.Markup);
        Assert.DoesNotContain("CancelTask", cut.Markup);
        // A finished task has nothing to follow.
        Assert.DoesNotContain("TaskInProgress", cut.Markup);
    }

    [Fact]
    public void AgentIncompatibleTask_ShowsTheBackendReasonAndItsOwnBadgeAndIcon()
    {
        const string reason = "L'agent du serveur « vps » (1.3.15) ne sait pas exécuter cette tâche "
            + "(opération PipelineFunction, capacité « pipeline.functions » absente) : mettez-le à jour en 1.3.16 "
            + "depuis la page du serveur (action « Mettre à jour »), puis relancez.";
        _handler.SetJsonResponse("api/tasks/7100", new ServerTaskDto
        {
            Id = 7100,
            ServerId = 9,
            ServerName = "vps",
            Name = "bump-version",
            Status = TaskExecutionStatus.Failed,
            ExitCode = -1,
            FailureCode = TaskFailureCodes.AgentIncompatible,
            FailureReason = reason,
            TimeoutSeconds = 300
        });
        _handler.SetJsonResponse("api/logs/task/7100", new List<TaskLogDto>
        {
            new()
            {
                Id = 1,
                TaskId = 7100,
                Level = TaskLogLevel.Error,
                Message = $"[incident:AgentIncompatible] {reason}",
                Timestamp = new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc)
            }
        });

        var cut = Render<TaskDetail>(parameters => parameters.Add(p => p.TaskId, 7100));
        cut.WaitForState(() => cut.Markup.Contains("bump-version"));

        Assert.Contains("AgentIncompatible", cut.Markup);
        Assert.Contains("mettez-le à jour en 1.3.16", cut.Markup);
        Assert.Contains("[incident:AgentIncompatible]", cut.Markup);
        Assert.Equal(OmniIconName.Download, TaskListView.GetFailureIcon(TaskFailureCodes.AgentIncompatible));
        Assert.Equal(OmniTone.Danger, TaskListView.GetFailureBadge(TaskFailureCodes.AgentIncompatible));
    }

    [Fact]
    public async Task R513_ARunningTask_ShowsItsLinesAsTheyArrive_ThenItsEnd()
    {
        _handler.SetJsonResponse("api/tasks/22358", new ServerTaskDto
        {
            Id = 22358,
            ServerId = 5,
            ServerName = "vps",
            Name = "Service Start - dovecot",
            Status = TaskExecutionStatus.Running
        });
        _handler.SetJsonResponse("api/logs/task/22358", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 22358, Message = "first line", Timestamp = DateTime.Now }
        });
        var cut = Render<TaskDetail>(parameters => parameters.Add(p => p.TaskId, 22358));
        cut.WaitForAssertion(() => Assert.Contains("first line", cut.Markup));
        Assert.True(cut.Instance.InFlight);
        Assert.Contains("TaskInProgress", cut.Markup);

        // A pushed batch: the new line is added, the one already on screen is not doubled, and a line
        // of another task is left out.
        cut.Instance.OnLogsReceived(
        [
            new() { Id = 1, TaskId = 22358, Message = "first line", Timestamp = DateTime.Now },
            new() { Id = 2, TaskId = 22358, Message = "pushed line", Timestamp = DateTime.Now },
            new() { Id = 3, TaskId = 99, Message = "other task", Timestamp = DateTime.Now }
        ]);
        cut.WaitForAssertion(() => Assert.Contains("pushed line", cut.Markup));
        Assert.Single(cut.FindAll(".log-entry"), entry => entry.TextContent.Contains("first line"));
        Assert.DoesNotContain("other task", cut.Markup);

        _handler.SetJsonResponse("api/tasks/22358", new ServerTaskDto
        {
            Id = 22358,
            ServerId = 5,
            ServerName = "vps",
            Name = "Service Start - dovecot",
            Status = TaskExecutionStatus.Failed,
            ExitCode = 5
        });
        _handler.SetJsonResponse("api/logs/task/22358", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 22358, Message = "first line", Timestamp = DateTime.Now },
            new() { Id = 2, TaskId = 22358, Message = "pushed line", Timestamp = DateTime.Now },
            new() { Id = 4, TaskId = 22358, Message = "Unit dovecot.service not found", Timestamp = DateTime.Now }
        });
        await cut.InvokeAsync(cut.Instance.ReloadFinishedAsync);

        cut.WaitForAssertion(() => Assert.Contains("Unit dovecot.service not found", cut.Markup));
        Assert.False(cut.Instance.InFlight);
        Assert.DoesNotContain("TaskInProgress", cut.Markup);
    }
}
