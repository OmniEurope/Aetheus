// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Tasks.Events;

/// <summary>Announces terminal pipeline-task status for the orchestrator to advance the run without
/// a Tasks-to-Pipelines dependency (ADR-039). Strict dispatch propagates handler
/// failures.</summary> <param name="PipelineRunId">Run the completed task belongs to.</param>
/// <param name="StageName"> Stage whose step completed, or <c>null</c> when the task carries no
/// step - an artifact collection task, which the run resumes from rather than advances. </param>
/// <param name="TaskStatus">Terminal status the task settled on.</param> <param
/// name="IsArtifactCollection"> Whether this was the run's artifact collection task. Stated rather
/// than inferred from a null stage, so a future task type without a step cannot silently take the
/// collection path. </param>
public sealed record PipelineStepTaskCompletedEvent(
    int PipelineRunId,
    string? StageName,
    TaskExecutionStatus TaskStatus,
    bool IsArtifactCollection) : IDomainEvent;
