// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// BACK-020: what a rerun will execute, resolved before it is authorized. <see cref="Preparation"/>
/// already carries the replayed parameters, so its <see cref="PipelineRunPreparation.TargetServerIds"/>
/// are the servers the rerun can reach; the launch then starts from this same plan.
/// </summary>
public sealed record PipelineRerunPlan(
    PipelineRunPreparation Preparation,
    Dictionary<string, string>? AdditionalVariables,
    Dictionary<string, string>? Parameters);
