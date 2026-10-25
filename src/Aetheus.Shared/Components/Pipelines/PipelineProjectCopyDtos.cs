// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary> The setup wizard's "start from an existing project". The source project's pipelines
/// are copied into the target project, adapted (name prefix, references to the copied pipelines,
/// libraries and vaults), never pointing at the source project's repository, environment or server.
/// </summary>
public sealed record PipelineProjectCopyRequest
{
    [Range(1, int.MaxValue)]
    public int SourceProjectId { get; init; }

    [Range(1, int.MaxValue)]
    public int TargetProjectId { get; init; }

    /// <summary>Name prefix of the copies, the wizard's pipeline prefix (lowercase, digits, hyphens).</summary>
    [Required]
    [StringLength(60, MinimumLength = 1)]
    [RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$")]
    public string Prefix { get; init; } = string.Empty;

    /// <summary>The source project's prefix the copies replace (the wizard proposes the slug of its name).</summary>
    [Required]
    [StringLength(60, MinimumLength = 1)]
    [RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$")]
    public string SourcePrefix { get; init; } = string.Empty;

    /// <summary>Branch of the new project's repository the copies are written to (the wizard sends the
    /// project's default branch, as for its own pipelines); null lets the repository decide.</summary>
    [StringLength(255)]
    public string? TargetBranch { get; init; }

    /// <summary>Source pipelines to copy. Empty asks for the plan of every pipeline of the source project.</summary>
    [MaxLength(200)]
    public List<int> PipelineIds { get; init; } = [];
}

/// <summary>What the copy does, or did: each pipeline with its new name and verdict, and the libraries
/// and vaults whose structure (names only, never values) is copied.</summary>
public sealed record PipelineProjectCopyPlanDto
{
    public List<PipelineCopyItemDto> Pipelines { get; init; } = [];
    public List<PipelineCopyResourceDto> Libraries { get; init; } = [];
    public List<PipelineCopyResourceDto> Vaults { get; init; } = [];
}

public sealed record PipelineCopyItemDto
{
    public int SourcePipelineId { get; init; }
    public string SourceName { get; init; } = string.Empty;
    public string NewName { get; init; } = string.Empty;
    public PipelineCopyStatus Status { get; init; }

    /// <summary>Why the copy is to review, or why it was not copied. Empty for a ready copy.</summary>
    public List<PipelineCopyIssueDto> Issues { get; init; } = [];

    /// <summary>The created pipeline, after the copy; null in a plan or when not copied.</summary>
    public int? CreatedPipelineId { get; init; }
}

public enum PipelineCopyStatus
{
    /// <summary>Copied and adapted; nothing in it names the source project any more.</summary>
    Ready = 0,

    /// <summary>Copied, but something in it could not be adapted safely: see the issues.</summary>
    ToReview = 1,

    /// <summary>Not copied: see the issues (name taken, definition invalid for the target project).</summary>
    NotCopied = 2
}

/// <summary>One reason a copy is to review or was not made. <see cref="Detail"/> holds the raw
/// tokens (names, values found); the wording is the caller's.</summary>
public sealed record PipelineCopyIssueDto
{
    public PipelineCopyIssueKind Kind { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public enum PipelineCopyIssueKind
{
    /// <summary>The definition still names the source project (paths, images, hosts...).</summary>
    SourceProjectNameRemains = 0,

    /// <summary>A stage or job pins a runner by its agent name.</summary>
    PinnedAgent = 1,

    /// <summary>An apache-proxy step publishes a host name (<c>server_name</c>), the source project's site.</summary>
    VirtualHostName = 2,

    /// <summary>The <c>source:</c> block names a repository.</summary>
    SourceRepository = 3,

    /// <summary>An address (URL or IP) is written in the definition.</summary>
    HardCodedHost = 4,

    /// <summary>The definition starts or reads a pipeline that is not part of the copy.</summary>
    UncopiedPipeline = 5,

    /// <summary>The adapted definition is not valid for the target project.</summary>
    InvalidDefinition = 6,

    /// <summary>The target project already has a pipeline of this name.</summary>
    NameTaken = 7,

    /// <summary>The source pipeline had a <c>schedule</c> or <c>webhook</c> trigger: the copy is created
    /// with <c>trigger: manual</c> so it does not run on its own. <see cref="PipelineCopyIssueDto.Detail"/>
    /// holds the original trigger; the user enables the copy explicitly.</summary>
    TriggerDisabled = 8,

    /// <summary>Identifiers the copy rewrote from the source project to the new one (variable values and
    /// environment names that are a bare identifier starting with the source prefix). Informational.</summary>
    IdentifiersRenamed = 9,

    /// <summary>The definition refers to files of the source repository (scripts, configurations,
    /// compose files): the copy does not carry them, the new project's repository has to provide them.</summary>
    RepositoryFileDependency = 10
}

/// <summary>A library or vault of the source project the copied pipelines use: its structure is copied
/// under <see cref="NewName"/>, with every value empty.</summary>
public sealed record PipelineCopyResourceDto
{
    public string SourceName { get; init; } = string.Empty;
    public string NewName { get; init; } = string.Empty;

    /// <summary>Variable names (library) or secret names (vault). Never a value.</summary>
    public List<string> Keys { get; init; } = [];

    /// <summary>The target project already has one of this name: it is kept as is, nothing is created.</summary>
    public bool AlreadyExists { get; init; }

    /// <summary>The created library or vault, after the copy.</summary>
    public int? CreatedId { get; init; }
}

/// <summary>The copy's outcome: what was done, and what the target project still lacks to run the
/// copies (the same readiness verdict as the project's pipelines page).</summary>
public sealed record PipelineProjectCopyResultDto
{
    public PipelineProjectCopyPlanDto Plan { get; init; } = new();
    public PipelineSetupReadinessDto Readiness { get; init; } = new();
}
