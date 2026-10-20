// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Tasks.Events;

/// <summary>Tasks publishes parsed telemetry for Pipelines to persist without an upward dependency
/// (ADR-039). Strict dispatch propagates failures rather than silently losing metrics.</summary>
/// <param name="PipelineRunId">Run the metrics belong to.</param> <param name="Metrics">Parsed
/// metrics, never empty - the emitter does not raise the event otherwise.</param>
public sealed record PipelineStepTaskMetricsCollectedEvent(
    int PipelineRunId,
    IReadOnlyList<RunMetric> Metrics) : IDomainEvent;
