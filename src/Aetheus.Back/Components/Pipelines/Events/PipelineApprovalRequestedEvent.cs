// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Pipelines.Events;

/// <summary>
/// Raised when a pipeline run reaches an approval gate and is awaiting human action.
/// Decouples the pipeline executor from notification / SignalR broadcast logic.
/// <paramref name="ApprovalId"/> names the approval itself, so the mail's decision links (recette R-24)
/// can never act on a later approval of the same run.
/// </summary>
public sealed record PipelineApprovalRequestedEvent(
    int PipelineRunId,
    string StageName,
    string? EnvironmentName,
    int ApprovalId) : IDomainEvent;
