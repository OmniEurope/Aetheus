// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Tasks;

public sealed record CreateTaskRequest
{
    public int ServerId { get; init; }

    [Required]
    [StringLength(100)]
    public string Name { get; init; } = string.Empty;

    [Required]
    [StringLength(10000)]
    public string Command { get; init; } = string.Empty;

    public ExecutorType Executor { get; init; } = ExecutorType.Shell;

    [Range(1, 86400)]
    public int TimeoutSeconds { get; init; } = 300;

    [BoundedDictionary(maxEntries: 64, maxKeyLength: 128, maxValueLength: 4096)]
    public Dictionary<string, string> EnvironmentVariables { get; init; } = [];
}

/// <summary>
/// F-32: enqueue a typed operation (no shell allow-list). The agent dispatches by
/// <see cref="Operation"/> and validates <see cref="Target"/> against per-kind rules.
/// </summary>
public sealed record CreateOperationRequest
{
    public int ServerId { get; init; }

    [Required]
    [StringLength(100)]
    public string Name { get; init; } = string.Empty;

    [Required]
    public OperationKind Operation { get; init; }

    [Required]
    [StringLength(256)]
    public string Target { get; init; } = string.Empty;

    [Range(1, 86400)]
    public int TimeoutSeconds { get; init; } = 300;
}

public sealed record TaskResultDto
{
    public int TaskId { get; init; }
    public TaskExecutionStatus Status { get; init; }
    public int ExitCode { get; init; }
    [StringLength(64)]
    public string? AgentSessionId { get; init; }
    public long? AgentSessionFencingToken { get; init; }
    // #53: legacy field - full task output is streamed via LogsController/SignalR, not the
    // result body. Kept for wire-compat with older agents that may still populate it.
    [StringLength(1_000_000)]
    public string? Output { get; init; }
    [StringLength(64)]
    public string? FailureCode { get; init; }
    [StringLength(512)]
    public string? FailureReason { get; init; }
}

public sealed record DeploymentBuildRefusalReport
{
    public Guid IncidentId { get; init; }
    public int TaskId { get; init; }
    public DateTime OccurredAtUtc { get; init; }
    [StringLength(512)]
    public string Reason { get; init; } = string.Empty;
}

public sealed record TaskCompletedNotification
{
    public int TaskId { get; init; }
    public int ServerId { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public TaskExecutionStatus Status { get; init; }
    public int? ExitCode { get; init; }
    public string? Output { get; init; }
    public OperationKind? Operation { get; init; }
}

public sealed record PendingTaskDto
{
    public int Id { get; init; }
    public string AgentSessionId { get; init; } = string.Empty;
    public long AgentSessionFencingToken { get; init; }
    public int? PipelineRunId { get; init; }
    /// <summary>
    /// True only for the control plane's system cleanup task. The agent uses it to remove
    /// daemon-visible container workspaces and per-run caches after the ordinary cleanup script.
    /// </summary>
    public bool PurgeWorkspace { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public ExecutorType Executor { get; init; }
    public int TimeoutSeconds { get; init; }
    [BoundedDictionary(maxEntries: 64, maxKeyLength: 128, maxValueLength: 4096)]
    public Dictionary<string, string> EnvironmentVariables { get; init; } = [];

    /// <summary>
    /// F-32: when set, the agent runs the typed operation pathway (no shell allow-list, no
    /// free-form command interpretation). <see cref="Command"/> is then reused as the operation
    /// target (e.g. container ID, service name) and validated by the dedicated executor.
    /// </summary>
    public OperationKind Operation { get; init; } = OperationKind.None;

    /// <summary>
    /// Phase 2 isolation: present only when <see cref="Executor"/> is <see cref="ExecutorType.Container"/>.
    /// Carries the image / runtime / network and the per-run workspace key the agent uses to derive
    /// the host bind-mount directory. <see cref="Command"/> is the shell script run inside the container.
    /// </summary>
    public ContainerSpec? Container { get; init; }
}

public sealed record AgentTaskLeaseRequest
{
    [StringLength(64)]
    public string AgentSessionId { get; init; } = string.Empty;

    public long AgentSessionFencingToken { get; init; }
}

/// <summary>
/// Describes the ephemeral container a container-isolated step runs in. The workspace key (the
/// pipeline run id) lets the agent place a stable host directory it bind-mounts at <c>/w</c>, so all
/// steps of a run share the cloned source while staying isolated from other runs.
/// </summary>
public sealed record ContainerSpec
{
    [StringLength(300)]
    public string Image { get; init; } = string.Empty;
    /// <summary>Optional key resolved from <c>.aetheus/toolchains.lock.yaml</c> after checkout.</summary>
    [StringLength(64)]
    public string? Toolchain { get; init; }
    /// <summary><c>bash</c> (default) or <c>sh</c>.</summary>
    [StringLength(20)]
    public string? Shell { get; init; }
    /// <summary><c>runc</c> (default), <c>runsc</c> (gVisor), or <c>kata</c>.</summary>
    [StringLength(40)]
    public string? Runtime { get; init; }
    /// <summary><c>none</c> (default) or <c>bridge</c>.</summary>
    [StringLength(40)]
    public string? Network { get; init; }
    /// <summary>S-UX-35: memory ceiling (<c>docker run --memory</c>, e.g. <c>512m</c>). Null = unbounded.</summary>
    [StringLength(20)]
    public string? Memory { get; init; }
    /// <summary>S-UX-35: CPU quota (<c>docker run --cpus</c>, e.g. <c>1.5</c>). Null = unbounded.</summary>
    [StringLength(20)]
    public string? Cpus { get; init; }
    public int WorkspaceKey { get; init; }
    /// <summary>Backend-derived organization/project boundary used to isolate reusable toolchain caches.</summary>
    [StringLength(128)]
    public string? CacheTrustDomain { get; init; }

    /// <summary>
    /// When true (the run's system cleanup step), the agent removes the per-run host workspace
    /// directory (<c>{WorkDir}/cw/{WorkspaceKey}</c>) after the step. The in-container
    /// <c>rm -rf /w</c> only empties the bind mount; without this the host dir would leak per run.
    /// </summary>
    public bool PurgeWorkspace { get; init; }
}
