// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Tasks.Events;

/// <summary>Tasks publishes the run warnings a step published in its <c>RUN_WARNING</c> output variable (recette R-75), for
/// Pipelines to put on the run page. Strict dispatch: a warning lost by a caught handler would leave a
/// run that validated nothing looking like a plain success.</summary>
/// <param name="PipelineRunId">Run whose step wrote the warnings.</param>
/// <param name="Warnings">The warnings, never empty.</param>
public sealed record PipelineStepRunWarningsReportedEvent(
    int PipelineRunId,
    IReadOnlyList<string> Warnings) : IDomainEvent;
