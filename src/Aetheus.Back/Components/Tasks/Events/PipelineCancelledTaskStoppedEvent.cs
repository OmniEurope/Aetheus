// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Tasks.Events;

/// <summary>Announces that an agent reported the process tree of a pipeline task the control plane had
/// cancelled as stopped (R-12), so the orchestrator can dispatch what it held back behind that task:
/// the cleanup and the always() stages. Strict dispatch, like the completion event.</summary>
/// <param name="PipelineRunId">Run the stopped task belongs to.</param>
public sealed record PipelineCancelledTaskStoppedEvent(int PipelineRunId) : IDomainEvent;
