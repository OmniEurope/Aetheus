// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data.Entities;

public class ServerTask
{
    public int Id { get; set; }
    public int ServerId { get; set; }

    /// <summary>
    /// ADR-060: the public identifier, a UUID v7 taken from <see cref="CreatedAt"/> so the unique index stays
    /// chronological. Set on every insert by <c>AppDbContext</c>. Null only on a row written by the previous
    /// version, until <c>TaskIdentityBackfillService</c> reaches it. <see cref="Id"/> stays the internal key.
    /// </summary>
    public Guid? Uid { get; set; }

    /// <summary>
    /// ADR-060: the number shown to a human, unique within <see cref="ServerId"/> and reserved from
    /// <see cref="Server.TaskCounter"/>. 0 means not numbered yet.
    /// </summary>
    public int Number { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public ExecutorType Executor { get; set; } = ExecutorType.Shell;

    /// <summary>
    /// F-32: when set, the task uses the typed-operation pathway on the agent. <see cref="Command"/>
    /// is then reused as the operation target (container ID, service name, etc.) and validated by
    /// the corresponding <c>IOperationExecutor</c>. <see cref="Executor"/> is ignored.
    /// </summary>
    public OperationKind Operation { get; set; } = OperationKind.None;

    public TaskExecutionStatus Status { get; set; } = TaskExecutionStatus.Pending;
    /// <summary>Durable workspace cleanup queued for the original runner while it is offline.</summary>
    public bool IsDeferredCleanup { get; set; }
    public int? PipelineRunId { get; set; }
    public int? PipelineStepRunId { get; set; }

    // Phase 2 isolation: when ContainerImage is set, the agent runs Command inside an ephemeral
    // hardened container (Executor = Container) instead of directly on the host. Runtime/Network
    // select the container runtime (runc/runsc/kata) and network policy (none/bridge).
    public string? ContainerImage { get; set; }
    public string? ContainerToolchain { get; set; }
    public string? ContainerShell { get; set; }
    public string? ContainerRuntime { get; set; }
    public string? ContainerNetwork { get; set; }
    // S-UX-35: optional per-run resource limits (docker run --memory / --cpus).
    public string? ContainerMemory { get; set; }
    public string? ContainerCpus { get; set; }
    public string EnvironmentVariables { get; set; } = "{}"; // JSON
    public int TimeoutSeconds { get; set; } = 300;
    public int? ExitCode { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AssignedAt { get; set; }
    /// <summary>Agent process lifetime that claimed this task. Null for legacy agents.</summary>
    public string? AssignedAgentSessionId { get; set; }
    /// <summary>Fencing token captured atomically when the task was assigned.</summary>
    public long? AssignedAgentSessionFencingToken { get; set; }
    /// <summary>Agent binary version captured atomically when this task is claimed.</summary>
    public string? AssignedAgentVersion { get; set; }
    /// <summary>Scanner-manifest contract captured from the claiming agent heartbeat.</summary>
    public string? AssignedScannerManifestSha256 { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// R-12: when the agent reported that a task the control plane had cancelled while it was running has
    /// stopped (its process tree is gone). Null until then, and for every task that was not cancelled
    /// while running. The scheduler holds a run's cleanup back while such a task is unacknowledged.
    /// </summary>
    public DateTime? StopAcknowledgedAt { get; set; }

    /// <summary>
    /// R-12: when the scheduler stopped waiting for this cancelled task's stop acknowledgement because the
    /// maximum wait elapsed without one. Not an acknowledgement: <see cref="StopAcknowledgedAt"/> stays null.
    /// It makes the "proceeding without the acknowledgement" warning fire once per task, and keeps later
    /// passes of the run from waiting on, or reporting, the same task again.
    /// </summary>
    public DateTime? StopWaitExpiredAt { get; set; }

    // Navigation
    public Server Server { get; set; } = null!;
    public PipelineRun? PipelineRun { get; set; }
    public PipelineStepRun? PipelineStepRun { get; set; }
    public List<TaskLog> Logs { get; set; } = [];
}
