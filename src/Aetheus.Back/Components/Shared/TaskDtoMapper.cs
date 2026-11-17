// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Shared;

internal static class TaskDtoMapper
{
    public static ServerTaskDto ToServerDetailDto(ServerTask task) =>
        Map(task, task.Command, string.Empty, ServerStatus.Online);

    /// <summary>The task as the live feed shows it, its command masked. <paramref name="aiTaskDefinitionId"/> is
    /// the AI task definition of an AI run (read from the task's protected environment by the caller, which holds the
    /// encryption service), so the AI task list can match the run to its row by id.</summary>
    public static ServerTaskDto ToMaskedDto(ServerTask task, int? aiTaskDefinitionId = null) =>
        Map(
            task,
            "[masked]",
            task.Server?.Name ?? string.Empty,
            task.Server?.Status ?? ServerStatus.Online) with
        { AiTaskDefinitionId = aiTaskDefinitionId };

    private static ServerTaskDto Map(
        ServerTask task,
        string command,
        string serverName,
        ServerStatus serverStatus) => new()
        {
            Id = task.Id,
            Uid = task.Uid,
            Number = task.Number,
            ServerId = task.ServerId,
            ServerName = serverName,
            ServerStatus = serverStatus,
            Name = task.Name,
            Command = command,
            Operation = task.Operation,
            Executor = task.Executor,
            Status = task.Status,
            PipelineRunId = task.PipelineRunId,
            PipelineStepRunId = task.PipelineStepRunId,
            CreatedAt = task.CreatedAt,
            StartedAt = task.StartedAt,
            CompletedAt = task.CompletedAt,
            ExitCode = task.ExitCode,
            FailureCode = task.FailureCode,
            FailureReason = task.FailureReason,
            TimeoutSeconds = task.TimeoutSeconds
        };
}
