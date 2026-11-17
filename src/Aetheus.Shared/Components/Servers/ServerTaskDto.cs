// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json.Serialization;

namespace Aetheus.Shared.Components.Servers;

public sealed record ServerTaskDto
{
    public int Id { get; init; }

    /// <summary>ADR-060: the public identifier. Null until the backfill reaches a task of an older version.</summary>
    public Guid? Uid { get; init; }

    /// <summary>ADR-060: the task's number on its server. 0 until the backfill reaches it.</summary>
    public int Number { get; init; }

    /// <summary>What a human reads: the number, or the key while the task has none yet.</summary>
    [JsonIgnore]
    public int DisplayNumber => Number > 0 ? Number : Id;

    public int ServerId { get; init; }
    public string ServerName { get; init; } = string.Empty;

    /// <summary>
    /// Status of the target server's agent. Lets the UI explain a stuck Pending/Assigned task:
    /// when this is <see cref="ServerStatus.Offline"/>, no agent is polling to claim it, so it
    /// will sit until the agent reconnects (or the timeout watchdog force-fails it).
    /// </summary>
    public ServerStatus ServerStatus { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public ExecutorType Executor { get; init; }
    public TaskExecutionStatus Status { get; init; }
    /// <summary>R-12: what kind of work the task is (<see cref="OperationKind.None"/> for a shell task), so a page
    /// finds its tasks by what they are and not by the text of their name.</summary>
    public OperationKind Operation { get; init; }

    /// <summary>R-12: the AI task definition an <see cref="OperationKind.AiRun"/> task runs, so the AI task list
    /// matches a run to its row by this id and a rename of the definition never loses the run. Filled for the live
    /// task feed (the queued event and the active list); null for any other task.</summary>
    public int? AiTaskDefinitionId { get; init; }

    public int? PipelineRunId { get; init; }
    /// <summary>R-8: that run's number within its pipeline.</summary>
    public int? PipelineRunNumber { get; init; }
    public int? PipelineStepRunId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public int? ExitCode { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }
    public int TimeoutSeconds { get; init; }
}
