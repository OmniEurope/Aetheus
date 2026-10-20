// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

public enum PipelineStatus
{
    Pending = 0,
    Running = 1,
    WaitingForApproval = 2,
    Success = 3,
    Failed = 4,
    Cancelled = 5,

    /// <summary>The run completed without blocking failures, but a continue_on_error step failed.
    /// This status does not trigger on_success chains.</summary>
    Partial = 6,

    /// <summary>The run failed, but every rollback step succeeded in restoring the preceding
    /// version. It does not trigger on_success chains and cannot be retried as a finished
    /// deployment attempt.</summary>
    RolledBack = 7,

    /// <summary>Marks a completed candidate as rejected when its assurance seal publishes
    /// CANDIDATE_DEPLOYABLE=false after a required control fails. It is distinct from technical
    /// failure and does not trigger on_success chains.</summary>
    Rejected = 8
}
