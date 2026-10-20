// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Tasks.Events;

/// <summary>Tasks publishes the expand/contract verdicts a migrate step reported, for Pipelines to keep
/// against the run (recette R1-43, ADR-039). Strict dispatch: a verdict lost by a caught handler would
/// leave the run page silent about a gate that did run.</summary>
/// <param name="PipelineRunId">Run the verdicts belong to.</param>
/// <param name="Verdicts">Parsed verdicts, never empty.</param>
public sealed record PipelineStepMigrationGateReportedEvent(
    int PipelineRunId,
    IReadOnlyList<PipelineMigrationGateVerdict> Verdicts) : IDomainEvent;
