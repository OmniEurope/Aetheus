// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>What a run recorded that tells which libraries and vaults it read: the definition it executed, the
/// public variables it resolved, and its project.</summary>
public sealed record PipelineRunVariableSource(
    string? YamlSnapshot, string? ResolvedVariablesJson, int? ProjectId, string? ProjectName);

/// <summary>Read port for <see cref="PipelineRunVariableSource"/>: three columns, not the whole run.</summary>
public interface IPipelineRunUsedVariablesReader
{
    /// <summary>The run's variable source, or null for an unknown run.</summary>
    Task<PipelineRunVariableSource?> GetSourceAsync(int runId, CancellationToken ct = default);
}
