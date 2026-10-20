// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data.Entities;

public class PipelineApproval
{
    public int Id { get; set; }
    public int PipelineRunId { get; set; }
    public string StageName { get; set; } = string.Empty;
    /// <summary>Who asked. An environment approval always names its environment; a pipeline
    /// approval names the stage's environment when it has one, and none otherwise.</summary>
    public ApprovalScope Scope { get; set; } = ApprovalScope.Environment;
    public int? EnvironmentId { get; set; }
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public DateTime RequestedAt { get; set; }

    /// <summary>Defines a stage-specific approval delay; null uses the environment
    /// ApprovalTimeoutMinutes.</summary>
    public int? TimeoutMinutes { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int? ResolvedByUserId { get; set; }
    public string? Comments { get; set; }

    // Navigation
    public PipelineRun PipelineRun { get; set; } = null!;
    public Environment? Environment { get; set; }
    public User? ResolvedByUser { get; set; }
}
