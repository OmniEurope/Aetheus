// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Git;

/// <summary>
/// Request to attach an external Git repository to a project as its mirror-backed source
/// (always available since recette R-295 removed the Features:ExternalRepos flag). Credentials are
/// write-only here - never echoed back in <see cref="ExternalRepoDto"/>.
/// </summary>
/// <remarks>
/// Pipeline clone: a runner clones the internal mirror over the backend's smart-HTTP path, never the
/// external remote. When a run's repository is a mirror on the current clone endpoint
/// (<c>MirrorCloneUrl.IsCurrentInternalClone</c>), <c>PipelineSystemTaskFactory</c> mints a run-scoped,
/// time-limited clone token (<c>GitRunCloneToken</c>, bound to the run and its project), registers it as
/// a masked runtime secret and injects it as <c>GIT_USERNAME</c> / <c>GIT_PASSWORD</c> into that clone
/// task only; for any other repository URL those two variables are removed from the clone task. A
/// pipeline therefore needs no durable credential to clone the mirror. The external credential supplied
/// here never reaches the agent: it stays in the backend, which uses it to import and refresh the mirror.
/// </remarks>
public sealed record AttachExternalRepoRequest : GitRepositoryCoordinatesRequest
{
    /// <summary>Required for self-hosted providers (Gitea / self-hosted GitLab); ignored for SaaS defaults.</summary>
    [StringLength(500)]
    public string? BaseUrl { get; init; }

    [StringLength(200)]
    public string? DefaultBranch { get; init; }

    public GitAuthType AuthType { get; init; }

    [StringLength(200)]
    public string? Username { get; init; }

    /// <summary>Empty for a public repository: HTTPS without a token is an anonymous read.</summary>
    [StringLength(4000)]
    public string? Token { get; init; }

    [StringLength(20000)]
    public string? PrivateKeyPem { get; init; }

    [StringLength(500)]
    public string? Passphrase { get; init; }

    [StringLength(8000)]
    public string? KnownHosts { get; init; }

    [Range(1, 1440)]
    public int FetchIntervalMinutes { get; init; } = 15;

    /// <summary>Push-back to the external remote - explicit opt-in (Phase 4).</summary>
    public bool WriteEnabled { get; init; }
}

/// <summary>
/// Read view of a project's external repository source. Carries no secrets.
/// </summary>
public sealed record ExternalRepoDto
{
    public int GitConnectionId { get; init; }
    public int ProjectId { get; init; }
    public GitProviderType ProviderType { get; init; }
    public string OwnerOrGroup { get; init; } = string.Empty;
    public string RepositoryName { get; init; } = string.Empty;
    public string? BaseUrl { get; init; }
    public GitAuthType AuthType { get; init; }
    public GitMirrorStatus MirrorStatus { get; init; }
    public DateTime? LastFetchedAt { get; init; }
    public string? LastFetchError { get; init; }
    public bool AutoSyncEnabled { get; init; }
    public int FetchIntervalMinutes { get; init; }
    public bool WriteEnabled { get; init; }
    public string? DefaultBranch { get; init; }
    public string? CloneUrl { get; init; }

    /// <summary>The mirror's slug: what a pipeline writes in <c>source: repository:</c>.</summary>
    public string? Slug { get; init; }

    /// <summary>True when the external repository is the project's own source; false when it is attached
    /// beside an internal repository, as an additional source for the pipelines that name it.</summary>
    public bool IsProjectSource { get; init; }
}
