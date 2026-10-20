// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Outcome of a git-first pipeline write. <see cref="NoRepo"/> means the project has no
/// internal repo (external/DB-only - a non-error fallback); <see cref="Failed"/> means an internal
/// repo exists but the commit/push could not be persisted (git is the source of truth, so the caller
/// must surface this rather than silently keeping a divergent DB definition).</summary>
public enum GitWriteOutcome
{
    Committed,
    NoRepo,
    Failed
}

public sealed record PipelineGitDefinitionLocation(
    int ProjectId, string PipelineName, string? SourceBranch, int? SourceRepositoryId = null);

/// <summary>
/// Git-strict storage for project-owned pipeline definitions: the authoritative YAML lives in the
/// project's internal repo under <c>.pipeline/</c>. The DB <c>Pipeline.YamlDefinition</c> is a
/// read-through mirror. Only internal (Aetheus-hosted) repos are supported in this slice;
/// external repos fall back to the DB definition.
/// </summary>
public interface IPipelineGitService
{
    /// <summary>
    /// Reads the authoritative YAML for a project-owned pipeline from the project's internal repo
    /// (<c>.pipeline/*.yaml</c>, matched by the YAML <c>name:</c>) on the selected source branch.
    /// Returns null when the project has no internal repo or no matching file - the caller then falls
    /// back to the DB definition.
    /// </summary>
    Task<string?> ReadProjectPipelineYamlAsync(int projectId, string pipelineName, CancellationToken ct = default,
        string? sourceBranch = null, int? sourceRepositoryId = null);

    /// <summary>Reads the pipeline YAML at an exact git revision so a pinned run uses one commit for
    /// both its definition and its workspace.</summary>
    Task<string?> ReadProjectPipelineYamlAtRevisionAsync(
        int projectId, string pipelineName, string revision, CancellationToken ct = default,
        int? sourceRepositoryId = null);

    /// <summary>Reads a versioned deployment configuration from <c>.pipeline/configs/**</c> at the
    /// exact run revision. Paths outside that directory and ambiguous repositories are rejected.</summary>
    Task<string?> ReadProjectConfigAtRevisionAsync(
        int projectId, string relativePath, string revision, CancellationToken ct = default,
        int? sourceRepositoryId = null);

    /// <summary>Reads the root <c>Directory.Build.props</c> at the exact run revision, whose
    /// <c>VersionPrefix</c> names the release series (<see cref="ReleaseSeries"/>). Null when the
    /// project has no internal repository, the revision is not an immutable commit, or the file is
    /// absent or too large.</summary>
    Task<string?> ReadVersionPropsAtRevisionAsync(
        int projectId, string revision, CancellationToken ct = default, int? sourceRepositoryId = null);

    /// <summary>Best-effort head commit SHA of the project's internal repo default branch, captured
    /// at run trigger time so the run records exactly which commit it built. Returns null when the
    /// project has no internal repo on disk or the commit cannot be resolved. Workspace pipelines
    /// treat that null as a blocking preflight failure.</summary>
    Task<string?> GetHeadCommitShaAsync(int projectId, CancellationToken ct = default,
        string? sourceBranch = null, int? sourceRepositoryId = null);

    /// <summary>Returns the authoritative internal-Git path, branch and current commit for a
    /// project-owned pipeline. Null means that the project has no internal repository.</summary>
    Task<PipelineSourceDto?> GetPipelineSourceAsync(int projectId, string pipelineName, CancellationToken ct = default,
        string? sourceBranch = null, int? sourceRepositoryId = null);

    /// <summary>
    /// Commits the YAML to <c>.pipeline/&lt;slug&gt;.yaml</c> on the selected source branch (or the
    /// repository default when none is selected). Git is the source of truth - this is the primary
    /// write, the DB row is the mirror.
    /// Returns <see cref="GitWriteOutcome.NoRepo"/> when the project has no internal repo (DB-only
    /// fallback), <see cref="GitWriteOutcome.Failed"/> with a reason when an internal repo exists but
    /// the commit/push could not be persisted, or <see cref="GitWriteOutcome.Committed"/> on success.
    /// </summary>
    Task<(GitWriteOutcome Outcome, string? Error)> WriteProjectPipelineYamlAsync(
        int projectId, string pipelineName, string yaml, string actor, CancellationToken ct = default,
        string? sourceBranch = null, int? sourceRepositoryId = null);

    /// <summary>
    /// Moves, rewrites or deletes an authoritative pipeline definition. A same-repository rename is
    /// one Git commit. Cross-repository moves compensate the destination if removing the source fails.
    /// </summary>
    Task<(GitWriteOutcome Outcome, string? Error)> ApplyProjectPipelineChangeAsync(
        PipelineGitDefinitionLocation? previous,
        PipelineGitDefinitionLocation? next,
        string? nextYaml,
        string actor,
        CancellationToken ct = default);

    /// <summary>
    /// Copies every pipeline owned by an environment into the project's internal repo
    /// <c>.pipeline/</c> (file name prefixed by the environment), as independent copies. Invoked when
    /// an environment is linked to a project. Returns the number of definitions written.
    /// </summary>
    Task<int> CopyEnvironmentPipelinesToProjectAsync(
        int environmentId, string environmentName, int projectId, string actor, CancellationToken ct = default);
}
