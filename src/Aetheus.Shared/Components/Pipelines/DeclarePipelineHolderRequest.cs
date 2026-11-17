// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// The body of <c>POST api/pipelines/{id}/declared-holders</c> (R-24): adds a vault or a variable library of the
/// pipeline's project to the <c>vaults:</c> or <c>variable_libraries:</c> list of its definition, so that a key the
/// holder already holds is read by the pipeline's runs.
/// </summary>
public sealed record DeclarePipelineHolderRequest
{
    /// <summary>A vault (<c>vaults:</c>) or a library (<c>variable_libraries:</c>).</summary>
    public PipelineSetupReadinessContainerKind Kind { get; init; }

    /// <summary>The name of the vault or library, as the project knows it.</summary>
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;
}
