// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Git;

/// <summary>Attaches an external repository as a mirror-backed project source with write-only
/// credentials.</summary> <remarks>External credentials stay on the control plane for mirror
/// refreshes; agents clone current internal mirror URLs with masked, run-scoped and time-limited
/// tokens supplied only to the clone task. Other clone URLs receive neither GIT_USERNAME nor
/// GIT_PASSWORD from this path.</remarks>
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
