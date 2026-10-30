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

    /// <summary>What the files of <see cref="InspectedBranch"/> and the fleet say about the pipelines that
    /// run with the templates' default commands. Each answer is null when it could not be told.</summary>
    public PipelineSetupDetectionDto Detection { get; init; } = new();

    public bool HasBlocking => Checks.Exists(check =>
        check.Severity == PipelineSetupReadinessSeverity.Blocking);
}

/// <summary>
/// What was found in the project's repository (branch files) and on its fleet for the templates whose
/// default commands need it. A property is null when the question was not asked (no selected template
/// depends on it) or could not be answered (repository not readable, a scan limit reached): never a
/// guess. The wizard reads it to propose its defaults and to warn before the pipelines are created.
/// </summary>
public sealed record PipelineSetupDetectionDto
{
    /// <summary>A <c>*.sln</c> or <c>*.slnx</c> within three levels of the repository root, which the
    /// default CI and quality commands build.</summary>
    public bool? HasDotNetSolution { get; init; }

    /// <summary>A .NET test project that is not a browser end-to-end project, which the default QA
    /// commands run.</summary>
    public bool? HasDotNetTestProject { get; init; }

    /// <summary>A <c>compose.yaml</c>, <c>compose.yml</c>, <c>docker-compose.yaml</c> or
    /// <c>docker-compose.yml</c> at the repository root, which the default production deployment starts.</summary>
    public bool? HasComposeFile { get; init; }

    /// <summary>An enrolled build runner of the organization reports Docker.</summary>
    public bool? HasDockerRunner { get; init; }

    /// <summary>A <c>.csproj</c> below the root (R-105), which the quality pipeline's complexity measure reads. Asked
    /// only when the quality pipeline is selected.</summary>
    public bool? HasDotNetProject { get; init; }

    /// <summary>A <c>.py</c> file below the root (R-105), which the quality pipeline lints with Ruff.</summary>
    public bool? HasPython { get; init; }

    /// <summary>A <c>.java</c> file below the root (R-105), which the quality pipeline checks with PMD.</summary>
    public bool? HasJava { get; init; }
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

    /// <summary>For an empty variable or secret, the id of the library or vault holding it, by item, so the
    /// summary links each one to its own page. An item whose holder could not be told has no entry.</summary>
    public Dictionary<string, int> ItemIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For an empty variable or secret (R-10), one row per value and pipeline that reads it: the
    /// library or vault holding the value, the key, and the pipeline. The table of the finding is these
    /// rows; <see cref="Items"/> stays the distinct keys.</summary>
    public List<PipelineSetupReadinessEmptyValueDto> EmptyValues { get; init; } = [];
}

/// <summary>One empty value a pipeline reads: where it is held, its key, and the pipeline that needs it (R-10).</summary>
public sealed record PipelineSetupReadinessEmptyValueDto
{
    /// <summary>The library or vault holding the value; null when it could not be told.</summary>
    public int? OwnerId { get; init; }

    /// <summary>Name of that library or vault; empty when it could not be told.</summary>
    [StringLength(200)]
    public string OwnerName { get; init; } = string.Empty;

    /// <summary>The variable or secret name.</summary>
    [StringLength(200)]
    public string Key { get; init; } = string.Empty;

    /// <summary>The pipeline that reads it; null for a template the setup wizard has not created yet.</summary>
    public int? PipelineId { get; init; }

    [StringLength(200)]
    public string PipelineName { get; init; } = string.Empty;
}

/// <summary>Describes libraries and vaults created for template requirements, with keys but empty
/// values for the operator to fill. No value is copied from another project.</summary>
public sealed record PipelineRequirementsProvisionResultDto
{
    public List<ProvisionedRequirementDto> Libraries { get; init; } = [];
    public List<ProvisionedRequirementDto> Vaults { get; init; } = [];
}

public sealed record ProvisionedRequirementDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>Keys (library) or secret names (vault) created.</summary>
    public List<string> Keys { get; init; } = [];

    /// <summary>The subset of <see cref="Keys"/> that received a value at creation (a generated secret, a
    /// port from the registry, a name derived from the project): the rest was created empty (R2-21).</summary>
    public List<string> FilledKeys { get; init; } = [];

    /// <summary>
    /// The same-named library or vault the key names were read from, as "project / name", or null when
    /// none readable exists: the resource is then created empty and says so, rather than inventing keys.
    /// </summary>
    public string? KeysCopiedFrom { get; init; }
}

/// <summary> A library or vault the project's existing pipelines declare in <c>requires:</c> and
/// the project does not have. Drives the "Getting started" library step and the overview warning.
/// </summary>
public sealed record UnmetRequirementDto
{
    /// <summary>"library" or "vault".</summary>
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>The pipelines that require it, so the warning can say who is waiting.</summary>
    public List<string> Pipelines { get; init; } = [];
}
