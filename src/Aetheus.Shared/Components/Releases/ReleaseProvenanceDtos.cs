// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Releases;

/// <summary> Where a release comes from and who used it afterwards, kept apart on purpose. <see
/// cref="CreatedBy"/> is the run that first published it; <see cref="Uses"/> are the later runs
/// that consumed it; <see cref="ArtifactInputs"/> and <see cref="Packages"/> are what its build
/// consumed, distinct from the deliverables it ships (<see cref="ReleaseDto.Artifacts"/>).
/// </summary>
public sealed record ReleaseProvenanceDto
{
    /// <summary>Null when the creator was never recorded (published before the tracking existed, or
    /// only detected from a branch).</summary>
    public ReleaseRunRefDto? CreatedBy { get; init; }

    /// <summary>The last run that recorded the release, set only when it is not the creator and the
    /// creator is unknown: it may be the creator or a later deployment, and nothing proves which.</summary>
    public ReleaseRunRefDto? LastRecordedBy { get; init; }

    /// <summary>The runs that produced the deliverables, other than the creator.</summary>
    public List<ReleaseRunRefDto> BuildRuns { get; init; } = [];

    public List<ReleaseUseDto> Uses { get; init; } = [];

    public List<ReleaseArtifactInputDto> ArtifactInputs { get; init; } = [];

    public List<ReleasePackageInputDto> Packages { get; init; } = [];

    /// <summary>Distinct packages found; <see cref="Packages"/> may hold fewer.</summary>
    public int PackageTotalCount { get; init; }
}

public sealed record ReleaseRunRefDto
{
    public int RunId { get; init; }
    public int BuildNumber { get; init; }
    public int PipelineId { get; init; }
    public string PipelineName { get; init; } = string.Empty;
    public PipelineStatus Status { get; init; }
    public DateTime StartedAt { get; init; }
}

public sealed record ReleaseUseDto
{
    public ReleaseRunRefDto Run { get; init; } = new();
    public ReleaseUseKind Kind { get; init; }
}

public enum ReleaseUseKind
{
    /// <summary>A deploy step shipped one of the release's artifacts.</summary>
    Deploy = 0,
    /// <summary>A restore-artifacts step brought one of the release's artifacts onto a runner.</summary>
    Restore = 1,
    /// <summary>A rollback redeployed this release.</summary>
    Rollback = 2,
    /// <summary>A later run recorded the release again (for example a deployment confirming it).</summary>
    Recorded = 3
}

public sealed record ReleaseArtifactInputDto
{
    public int ConsumerRunId { get; init; }
    public string StepName { get; init; } = string.Empty;
    public ArtifactInputKind Kind { get; init; }
    /// <summary>Null once artifact retention deleted it; the name and digest remain.</summary>
    public int? ArtifactId { get; init; }
    public string ArtifactName { get; init; } = string.Empty;
    public string? Sha256 { get; init; }
    public int SourcePipelineRunId { get; init; }
    public int? SourceReleaseId { get; init; }
    public string? SourceReleaseVersion { get; init; }
    /// <summary>True when the consumed artifact is also one of this release's deliverables.</summary>
    public bool IsDeliverable { get; init; }
}

public sealed record ReleasePackageInputDto
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? PackageUrl { get; init; }
    public bool IsDirect { get; init; }
    /// <summary>The run whose SBOM listed the package.</summary>
    public int PipelineRunId { get; init; }
}
