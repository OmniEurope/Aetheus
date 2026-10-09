// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data.Entities;

public class GitConnection
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public GitProviderType ProviderType { get; set; }
    public string OwnerOrGroup { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public int? ServiceConnectionId { get; set; }
    public bool AutoSyncEnabled { get; set; } = true;
    public DateTime? LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // External-Git parity (always on since recette R-295). Base URL for self-hosted providers
    // (e.g. https://gitea.example.com); null for the SaaS defaults (github.com / gitlab.com).
    public string? BaseUrl { get; set; }

    // Server-side "credited mirror" - a bare `git clone --mirror` refreshed by fetch.
    // The UI and pipeline clones consume this local mirror via the internal smart-HTTP path,
    // so external repos inherit full parity with internal ones.
    public string? MirrorPath { get; set; }
    public GitMirrorStatus MirrorStatus { get; set; } = GitMirrorStatus.NotInitialized;
    public int FetchIntervalMinutes { get; set; } = 15;
    public string? LastFetchError { get; set; }
    public DateTime? LastFetchedAt { get; set; }

    // Push-back to the external remote is an explicit opt-in per connection (Phase 4).
    public bool WriteEnabled { get; set; }

    // Navigation
    public Project Project { get; set; } = null!;
    public ServiceConnection? ServiceConnection { get; set; }
    public List<PullRequest> PullRequests { get; set; } = [];
    public List<BranchPolicy> BranchPolicies { get; set; } = [];
}
