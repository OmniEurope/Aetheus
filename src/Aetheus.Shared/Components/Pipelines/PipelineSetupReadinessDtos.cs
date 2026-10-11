// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// What the setup wizard is about to create, so the readiness check can be derived from the very
/// templates that will be applied rather than from a hard-coded idea of what a pipeline needs.
/// </summary>
public sealed record PipelineSetupReadinessRequest
{
    [Range(1, int.MaxValue)]
    public int ProjectId { get; init; }

    /// <summary>Template names exactly as the wizard resolved them (application-ci, application-qa, ...).</summary>
    [MinLength(1)]
    [MaxLength(50)]
    [MaxItemStringLength(200)]
    public List<string> TemplateNames { get; init; } = [];
}

/// <summary>
/// The verdict of the pre-creation readiness check. <see cref="Checks"/> holds only what is actually
/// wrong: an empty list means nothing was found, which is not the same as "everything was checked" -
/// see <see cref="RepositoryInspected"/>.
/// </summary>
public sealed record PipelineSetupReadinessDto
{
    public List<PipelineSetupReadinessCheckDto> Checks { get; init; } = [];

    /// <summary>
    /// False when the repository side could not be read at all (no internal repository, an external
    /// repository Aetheus holds no readable copy of, or the branch does not exist). The caller must then say the file checks were skipped instead of implying the
    /// files are there.
    /// </summary>
    public bool RepositoryInspected { get; init; }

    /// <summary>Branch the file checks were run against; null when none was inspected.</summary>
    public string? InspectedBranch { get; init; }

    public bool HasBlocking => Checks.Exists(check =>
        check.Severity == PipelineSetupReadinessSeverity.Blocking);
}

/// <summary>
/// One unmet requirement. The backend carries the <see cref="Kind"/> and the raw
/// <see cref="Items"/> (paths, environment names); the wording is the caller's, so the UI stays
/// localizable instead of rendering server-side English.
/// </summary>
public sealed record PipelineSetupReadinessCheckDto
{
    public PipelineSetupReadinessKind Kind { get; init; }
    public PipelineSetupReadinessSeverity Severity { get; init; }

    /// <summary>Machine tokens the message needs: missing script paths, environment names, template names.</summary>
    public List<string> Items { get; init; } = [];
}

/// <summary>
/// PLAN-003 lot 30 / D22: what the one-click "create what the templates require" produced. Every
/// library and vault is created in the project with its keys and EMPTY values: the operator types the
/// values afterwards. Nothing here is a value copied from another project.
/// </summary>
public sealed record PipelineRequirementsProvisionResultDto
{
    public List<ProvisionedRequirementDto> Libraries { get; init; } = [];
    public List<ProvisionedRequirementDto> Vaults { get; init; } = [];
}

public sealed record ProvisionedRequirementDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>Keys (library) or secret names (vault) created, all with an empty value.</summary>
    public List<string> Keys { get; init; } = [];

    /// <summary>
    /// The same-named library or vault the key names were read from, as "project / name", or null when
    /// none readable exists: the resource is then created empty and says so, rather than inventing keys.
    /// </summary>
    public string? KeysCopiedFrom { get; init; }
}

/// <summary>
/// PLAN-003 lot 30: a library or vault the project's existing pipelines declare in <c>requires:</c>
/// and the project does not have. Drives the "Getting started" library step and the overview warning.
/// </summary>
public sealed record UnmetRequirementDto
{
    /// <summary>"library" or "vault".</summary>
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>The pipelines that require it, so the warning can say who is waiting.</summary>
    public List<string> Pipelines { get; init; } = [];
}
