// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Servers;

public sealed record ServerTaskDto
{
    public int Id { get; init; }
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
    public int? PipelineRunId { get; init; }
    public int? PipelineStepRunId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public int? ExitCode { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureReason { get; init; }
    public int TimeoutSeconds { get; init; }
}
