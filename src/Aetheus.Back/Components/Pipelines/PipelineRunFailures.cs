// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>What one read of a run's failed steps says when the run ends.</summary>
/// <param name="HasBlocking">True when a step failed or timed out without <c>continue_on_error</c>.</param>
/// <param name="Continuable">The failed or timed-out steps that had <c>continue_on_error</c> (read-only copies).</param>
public sealed record PipelineRunFailures(bool HasBlocking, List<PipelineStepRun> Continuable);
