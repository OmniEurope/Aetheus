// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Extensions;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Aetheus.Back.Components.Git;

public class GitLightService(
    IGitLightRepository lightRepo,
    IGitRepository gitRepo,
    IGitLightCliService cli,
    IAuditService audit,
    IOptions<GitLightOptions> options,
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration,
    IHubContext<GitRealtimeHub> hub,
    IMemoryCache cache,
    GitBranchProtectionService branchProtection,
    ILogger<GitLightService> logger,
    TimeProvider timeProvider) : IGitLightService
{
    internal const int MaximumCommitPatchFiles = 100;
    private readonly GitLightOptions _options = options.Value;

    private Task BroadcastAsync(int repoId, string @event, CancellationToken ct = default) =>
        hub.Clients.Group(GitRealtimeGroups.Repository(repoId)).SendAsync(@event, repoId, ct);

    // ── Repository CRUD ──────────────────────────────────────────────

    public async Task<List<GitLightRepoDto>> GetRepositoriesAsync(int projectId, CancellationToken ct = default)
    {
        var repos = await lightRepo.GetByProjectAsync(projectId, ct).ConfigureAwait(false);
        var changed = 0;
        var semaphore = new SemaphoreSlim(4);
        await Task.WhenAll(repos.Select(async repo =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await SyncDefaultBranchAsync(repo, ct).ConfigureAwait(false))
                    Interlocked.Increment(ref changed);
            }
            finally { semaphore.Release(); }
        })).ConfigureAwait(false);
        if (changed > 0)
            await lightRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        return repos.Select(r => GitLightMapper.MapRepoToDto(r, BuildCloneUrl(r.ProjectId, r.Slug))).ToList();
    }

    public async Task<List<GitLightRepoDto>> GetAccessibleRepositoriesAsync(List<int>? accessibleProjectIds, CancellationToken ct = default)
    {
        var repos = await lightRepo.GetAccessibleAsync(accessibleProjectIds, ct).ConfigureAwait(false);
        // No per-repo default-branch sync here: this is a cross-project list view (potentially many
        // repos) and a git shell-out per repo would make the page load O(n) processes. The drift is
        // reconciled when a repo is actually opened (GetRepositoryAsync). Keep the list cheap.
        return repos.Select(r => GitLightMapper.MapRepoToDto(r, BuildCloneUrl(r.ProjectId, r.Slug))).ToList();
    }

    public async Task<PaginatedResult<GitLightRepoDto>> GetRepositoriesPageAsync(
        int? projectId, List<int>? accessibleProjectIds, PaginationRequest request,
        CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        if (accessibleProjectIds is { Count: 0 })
            return new PaginatedResult<GitLightRepoDto> { Page = page, PageSize = pageSize };

        var (items, totalCount) = await lightRepo.GetAccessiblePagedAsync(
            accessibleProjectIds, projectId, request.Search, request.SortBy,
            request.SortDescending, page, pageSize, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<GitLightRepoDto>
        {
            Items = items.Select(r => GitLightMapper.MapRepoToDto(
                r, BuildCloneUrl(r.ProjectId, r.Slug))).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<GitLightRepoDto?> GetRepositoryAsync(int id, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdWithProjectAsync(id, ct).ConfigureAwait(false);
        if (entity is null) return null;
        if (await SyncDefaultBranchAsync(entity, ct).ConfigureAwait(false))
            await lightRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        return GitLightMapper.MapRepoToDto(entity, BuildCloneUrl(entity.ProjectId, entity.Slug));
    }

    private async Task<bool> SyncDefaultBranchAsync(GitInternalRepo r, CancellationToken ct)
    {
        var diskPath = ResolveDiskPath(r.ProjectId, r.Slug);
        var detected = await cli.DetectDefaultBranchAsync(diskPath, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(detected) || string.Equals(detected, r.DefaultBranch, StringComparison.Ordinal))
            return false;

        logger.LogInformation("Default branch drift for repo {RepoId} ({Slug}): stored={Stored}, detected={Detected} - updating.",
            r.Id, r.Slug, r.DefaultBranch, detected);
        r.DefaultBranch = detected;
        return true;
    }

    public async Task<GitLightRepoDto> CreateRepositoryAsync(CreateGitLightRepoRequest request, CancellationToken ct = default)
    {
        // Guard the FK before touching disk: an unknown ProjectId otherwise surfaces as a raw
        // 23503 foreign-key violation (HTTP 500) at SaveChangesAsync - and only after a bare
        // repo has already been created on disk. Fail clean (404) and early instead.
        if (!await lightRepo.ProjectExistsAsync(request.ProjectId, ct).ConfigureAwait(false))
            throw new NotFoundException($"Project {request.ProjectId} not found.");

        var slug = GitLightMapper.GenerateSlug(request.Name);
        var fallbackBranch = await lightRepo.GetProjectDefaultBranchAsync(request.ProjectId, ct).ConfigureAwait(false);
        var defaultBranch = !string.IsNullOrWhiteSpace(request.DefaultBranch) ? request.DefaultBranch
            : !string.IsNullOrWhiteSpace(fallbackBranch) ? fallbackBranch
            : "main";
        var diskPath = ResolveDiskPath(request.ProjectId, slug);

        await cli.InitBareRepoAsync(diskPath, defaultBranch, ct).ConfigureAwait(false);

        var entity = new GitInternalRepo
        {
            ProjectId = request.ProjectId,
            Name = request.Name,
            Slug = slug,
            Description = request.Description,
            DefaultBranch = defaultBranch
        };

        await lightRepo.AddAsync(entity, ct).ConfigureAwait(false);

        // Auto-create GitConnection linked to this internal repo
        var connection = new GitConnection
        {
            ProjectId = request.ProjectId,
            ProviderType = GitProviderType.AetheusGit,
            OwnerOrGroup = request.ProjectId.ToString(),
            RepositoryName = slug,
            AutoSyncEnabled = false
        };

        await gitRepo.AddConnectionAsync(connection, ct).ConfigureAwait(false);

        entity.GitConnectionId = connection.Id;
        await lightRepo.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.LogAsync("Created", "GitInternalRepo", entity.Id, $"{request.Name} ({slug})", ct).ConfigureAwait(false);

        entity = await lightRepo.FindByIdWithProjectAsync(entity.Id, ct).ConfigureAwait(false);
        return GitLightMapper.MapRepoToDto(entity!, BuildCloneUrl(entity!.ProjectId, entity.Slug));
    }

    public async Task EnsureRepositoryInitializedAsync(int id, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(id, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Repository {id} not found.");
        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        await cli.InitBareRepoAsync(diskPath, entity.DefaultBranch, ct).ConfigureAwait(false);
    }

    public async Task<GitLightRepoDto?> UpdateRepositoryAsync(int id, UpdateGitLightRepoRequest request, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(id, ct).ConfigureAwait(false);
        if (entity is null) return null;
        var defaultBranchChanged = false;

        if (request.Description is not null)
            entity.Description = request.Description;

        var requestedDefaultBranch = request.DefaultBranch?.Trim();
        if (!string.IsNullOrWhiteSpace(requestedDefaultBranch)
            && !string.Equals(requestedDefaultBranch, entity.DefaultBranch, StringComparison.Ordinal))
        {
            var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
            if (!entity.IsEmpty)
            {
                var branches = await cli.GetBranchesAsync(diskPath, entity.DefaultBranch, ct).ConfigureAwait(false);
                var matchingBranch = branches.FirstOrDefault(
                    branch => string.Equals(branch.Name, requestedDefaultBranch, StringComparison.OrdinalIgnoreCase));
                if (matchingBranch is null)
                    throw new BadRequestException($"Branch '{requestedDefaultBranch}' does not exist.");
                requestedDefaultBranch = matchingBranch.Name;
            }

            await cli.SetHeadAsync(diskPath, requestedDefaultBranch, ct).ConfigureAwait(false);
            entity.DefaultBranch = requestedDefaultBranch;
            defaultBranchChanged = true;
        }

        entity.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await lightRepo.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "GitInternalRepo", id, null, ct).ConfigureAwait(false);

        await BroadcastAsync(id, GitRealtimeEvents.RepositoryChanged, ct).ConfigureAwait(false);
        if (defaultBranchChanged)
            await BroadcastAsync(id, GitRealtimeEvents.BranchesChanged, ct).ConfigureAwait(false);

        entity = await lightRepo.FindByIdWithProjectAsync(id, ct).ConfigureAwait(false);
        return GitLightMapper.MapRepoToDto(entity!, BuildCloneUrl(entity!.ProjectId, entity.Slug));
    }

    public async Task<bool> DeleteRepositoryAsync(int id, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(id, ct).ConfigureAwait(false);
        if (entity is null) return false;

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        try
        {
            await cli.DeleteRepoAsync(diskPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete repo on disk at {Path}, proceeding with DB cleanup", diskPath);
        }

        // Remove linked GitConnection (cascades to PullRequests/BranchPolicies)
        if (entity.GitConnectionId.HasValue)
        {
            var connection = await gitRepo.FindConnectionAsync(entity.GitConnectionId.Value, ct).ConfigureAwait(false);
            if (connection is not null)
                await gitRepo.RemoveConnectionAsync(connection, ct).ConfigureAwait(false);
        }

        await lightRepo.RemoveAsync(entity, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "GitInternalRepo", id, entity.Name, ct).ConfigureAwait(false);
        return true;
    }

    // ── Commits ──────────────────────────────────────────────────────

    public async Task<PaginatedResult<GitLightCommitDto>> GetCommitsAsync(
        int repoId, string? refName, int page, int pageSize, string? search = null, CancellationToken ct = default,
        GitCommitLogFilter? filter = null)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return new PaginatedResult<GitLightCommitDto> { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        var effectiveRef = string.Equals(refName, GitReference.AllBranches, StringComparison.Ordinal)
            ? null
            : refName ?? entity.DefaultBranch;
        page = Math.Max(page, 1);
        pageSize = PaginationDefaults.Clamp(pageSize);

        // Page items are always fresh; the expensive total is cache-backed and
        // runs concurrently with the page walk (one git shell-out on a count hit).
        var itemsTask = cli.GetCommitsAsync(diskPath, effectiveRef, (page - 1) * pageSize, pageSize, search, ct: ct, filter: filter);
        var totalCount = await GitCommitCountCache.GetAsync(cache, cli, repoId, diskPath, effectiveRef, search, ct, filter).ConfigureAwait(false);
        var items = await itemsTask.ConfigureAwait(false);

        return new PaginatedResult<GitLightCommitDto> { Items = items, TotalCount = totalCount, Page = page, PageSize = pageSize };
    }

    // O: structured graph data (commits + parent SHAs across all refs) for the SVG lane renderer.
    // Reuses the commit walk with refName=null so the CLI passes --all.
    public async Task<List<GitLightCommitDto>> GetCommitGraphDataAsync(int repoId, int maxCount, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return [];

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        return await cli.GetCommitsAsync(diskPath, null, 0, Math.Clamp(maxCount, 1, 500), null, ct: ct).ConfigureAwait(false);
    }

    // Single commit + its file-level diff (against the first parent, or the empty tree for a root
    // commit). Reuses the commit walk for metadata and GetCommitPatchAsync for the unified patch.
    public async Task<GitLightCommitDetailDto?> GetCommitDetailAsync(int repoId, string sha, CancellationToken ct = default)
    {
        if (!GitUnifiedDiffParser.IsSha(sha)) return null;
        var normalizedSha = sha.ToLowerInvariant();

        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return null;

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        var commits = await cli.GetCommitsAsync(diskPath, normalizedSha, 0, 1, ct: ct).ConfigureAwait(false);
        var commit = commits.FirstOrDefault();
        if (commit is null) return null;

        var patch = commit.ParentShas.Count > 0
            ? await cli.GetCommitPatchAsync(diskPath, commit.ParentShas[0], commit.Sha, ct).ConfigureAwait(false)
            : await cli.GetRootCommitPatchAsync(diskPath, commit.Sha, ct).ConfigureAwait(false);
        return new GitLightCommitDetailDto
        {
            Commit = commit,
            Diff = GitUnifiedDiffParser.Parse(
                patch.Patch,
                MaximumCommitPatchFiles,
                patch.IsTruncated)
        };
    }

    public async Task<Dictionary<string, string>> GetCommitMessagesAsync(int repoId, IReadOnlyCollection<string> shas, CancellationToken ct = default)
    {
        if (shas.Count is < 1 or > 200 || shas.Any(sha => !GitUnifiedDiffParser.IsSha(sha)))
            throw new ArgumentException("Between 1 and 200 hexadecimal commit SHAs are required.", nameof(shas));
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return [];
        return await cli.GetCommitMessagesAsync(ResolveDiskPath(entity.ProjectId, entity.Slug), shas, ct).ConfigureAwait(false);
    }

    // ── Branches ─────────────────────────────────────────────────────
    public async Task<List<GitLightBranchDto>> GetBranchesAsync(int repoId, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return [];

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        return await cli.GetBranchesAsync(diskPath, entity.DefaultBranch, ct).ConfigureAwait(false);
    }

    public async Task CreateBranchAsync(int repoId, CreateGitLightBranchRequest request, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(entity);

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        await cli.CreateBranchAsync(diskPath, request.Name, request.StartRef, ct).ConfigureAwait(false);
        await audit.LogAsync("CreatedBranch", "GitInternalRepo", repoId, request.Name, ct).ConfigureAwait(false);
        await BroadcastAsync(repoId, GitRealtimeEvents.BranchesChanged, ct).ConfigureAwait(false);
    }

    public async Task DeleteBranchAsync(int repoId, string name, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(entity);

        var rules = await lightRepo.GetBranchProtectionRulesAsync(repoId, ct).ConfigureAwait(false);
        var matchingRule = rules.FirstOrDefault(r => r.PreventDeletion && GitBranchPatternMatcher.Matches(name, r.Pattern));
        if (matchingRule is not null)
            throw new InvalidOperationException($"Branch '{name}' is protected by rule '{matchingRule.Pattern}' and cannot be deleted.");

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        await cli.DeleteBranchAsync(diskPath, name, ct).ConfigureAwait(false);
        await audit.LogAsync("DeletedBranch", "GitInternalRepo", repoId, name, ct).ConfigureAwait(false);
        await BroadcastAsync(repoId, GitRealtimeEvents.BranchesChanged, ct).ConfigureAwait(false);
    }

    // ── Tags ─────────────────────────────────────────────────────────

    public async Task<List<GitLightTagDto>> GetTagsAsync(int repoId, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return [];

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        return await cli.GetTagsAsync(diskPath, ct).ConfigureAwait(false);
    }

    public async Task CreateTagAsync(int repoId, CreateGitLightTagRequest request, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(entity);

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        await cli.CreateTagAsync(diskPath, request.Name, request.Ref, request.Message, ct).ConfigureAwait(false);
        await audit.LogAsync("CreatedTag", "GitInternalRepo", repoId, request.Name, ct).ConfigureAwait(false);
        await BroadcastAsync(repoId, GitRealtimeEvents.TagsChanged, ct).ConfigureAwait(false);
    }

    public async Task DeleteTagAsync(int repoId, string name, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(entity);

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        await cli.DeleteTagAsync(diskPath, name, ct).ConfigureAwait(false);
        await audit.LogAsync("DeletedTag", "GitInternalRepo", repoId, name, ct).ConfigureAwait(false);
        await BroadcastAsync(repoId, GitRealtimeEvents.TagsChanged, ct).ConfigureAwait(false);
    }

    // ── File browser ─────────────────────────────────────────────────

    public async Task<List<GitLightTreeEntryDto>> GetTreeAsync(int repoId, string? refName, string? path, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return [];

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        var effectiveRef = refName ?? entity.DefaultBranch;
        return await cli.GetTreeAsync(diskPath, effectiveRef, path, ct).ConfigureAwait(false);
    }

    public async Task<GitLightBlobDto?> GetBlobAsync(int repoId, string refName, string path, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return null;

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        return await cli.GetBlobAsync(diskPath, refName, path, ct).ConfigureAwait(false);
    }

    public async Task<Stream?> GetBlobStreamAsync(int repoId, string refName, string path, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return null;

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        return await cli.GetBlobStreamAsync(diskPath, refName, path, ct).ConfigureAwait(false);
    }

    // ── Internal Pull Requests ───────────────────────────────────────

    public async Task<PaginatedResult<InternalPullRequestDto>> GetPullRequestsAsync(
        int repoId, PullRequestPaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null || !entity.GitConnectionId.HasValue)
            return new PaginatedResult<InternalPullRequestDto> { Items = [], TotalCount = 0, Page = page, PageSize = pageSize };

        var (items, totalCount) = await gitRepo.GetPullRequestsPagedAsync(
            entity.GitConnectionId.Value, request.Search, page, pageSize, request.Status, ct,
            GitLightMapper.MapPrSortKey(request.SortBy), request.SortDescending, request.Filters).ConfigureAwait(false);

        return new PaginatedResult<InternalPullRequestDto>
        {
            Items = items.Select(GitLightMapper.MapPrToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<InternalPullRequestDto?> GetPullRequestAsync(int repoId, int prId, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null || !entity.GitConnectionId.HasValue) return null;

        var pr = await gitRepo.FindPullRequestByExternalIdAsync(entity.GitConnectionId.Value, prId, ct).ConfigureAwait(false);
        return pr is null ? null : GitLightMapper.MapPrToDto(pr);
    }

    public async Task<InternalPullRequestDto> CreatePullRequestAsync(
        int repoId, CreateInternalPullRequestRequest request, string authorLogin, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(entity);

        if (!entity.GitConnectionId.HasValue)
            throw new InvalidOperationException("Internal repo has no linked GitConnection.");

        var prNumber = await lightRepo.GetNextPrNumberAsync(entity.GitConnectionId.Value, ct).ConfigureAwait(false);

        var pr = new PullRequest
        {
            GitConnectionId = entity.GitConnectionId.Value,
            ExternalId = prNumber,
            Title = request.Title,
            Description = request.Description,
            SourceBranch = request.SourceBranch,
            TargetBranch = request.TargetBranch,
            AuthorLogin = authorLogin,
            Status = PullRequestStatus.Open,
            ExternalCreatedAt = timeProvider.GetUtcNow().UtcDateTime
        };

        // Get head commit SHA from source branch
        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        var commits = await cli.GetCommitsAsync(diskPath, request.SourceBranch, 0, 1, null, ct: ct).ConfigureAwait(false);
        if (commits.Count > 0)
            pr.HeadCommitSha = commits[0].Sha;

        await gitRepo.AddPullRequestAsync(pr, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "PullRequest", pr.Id, $"#{prNumber}: {request.Title}", ct).ConfigureAwait(false);
        await BroadcastAsync(repoId, GitRealtimeEvents.PullRequestsChanged, ct).ConfigureAwait(false);

        return GitLightMapper.MapPrToDto(pr);
    }

    public async Task<InternalPullRequestDto?> MergePullRequestAsync(
        int repoId, int prId, string authorLogin, CancellationToken ct = default)
    {
        var (entity, pr) = await GitPullRequestResolver
            .FindOpenAsync(lightRepo, gitRepo, repoId, prId, ct).ConfigureAwait(false);
        if (entity is null || pr is null) return null;

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        var (success, mergeCommitSha, error) = await cli.MergeBranchesAsync(
            diskPath, pr.SourceBranch, pr.TargetBranch, authorLogin, $"{authorLogin}@aetheus", ct).ConfigureAwait(false);

        if (!success)
        {
            logger.LogWarning("Merge failed for PR #{PrNumber}: {Error}", prId, error);
            throw new InvalidOperationException($"Merge failed: {error}");
        }

        pr.Status = PullRequestStatus.Merged;
        pr.MergeCommitSha = mergeCommitSha;
        pr.ExternalMergedAt = timeProvider.GetUtcNow().UtcDateTime;
        pr.LastSyncedAt = timeProvider.GetUtcNow().UtcDateTime;
        await gitRepo.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.LogAsync("Merged", "PullRequest", pr.Id, $"#{prId}", ct).ConfigureAwait(false);
        await BroadcastAsync(repoId, GitRealtimeEvents.PullRequestsChanged, ct).ConfigureAwait(false);
        await BroadcastAsync(repoId, GitRealtimeEvents.BranchesChanged, ct).ConfigureAwait(false);
        await BroadcastAsync(repoId, GitRealtimeEvents.CommitsChanged, ct).ConfigureAwait(false);
        return GitLightMapper.MapPrToDto(pr);
    }

    public async Task<InternalPullRequestDto?> ClosePullRequestAsync(int repoId, int prId, CancellationToken ct = default)
    {
        var (_, pr) = await GitPullRequestResolver
            .FindOpenAsync(lightRepo, gitRepo, repoId, prId, ct).ConfigureAwait(false);
        if (pr is null) return null;

        pr.Status = PullRequestStatus.Closed;
        pr.LastSyncedAt = timeProvider.GetUtcNow().UtcDateTime;
        await gitRepo.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.LogAsync("Closed", "PullRequest", pr.Id, $"#{prId}", ct).ConfigureAwait(false);
        await BroadcastAsync(repoId, GitRealtimeEvents.PullRequestsChanged, ct).ConfigureAwait(false);
        return GitLightMapper.MapPrToDto(pr);
    }

    public async Task<PullRequestDiffDto> GetPullRequestDiffAsync(int repoId, int prId, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null || !entity.GitConnectionId.HasValue)
            return new PullRequestDiffDto();

        var pr = await gitRepo.FindPullRequestByExternalIdAsync(entity.GitConnectionId.Value, prId, ct).ConfigureAwait(false);
        if (pr is null) return new PullRequestDiffDto();

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        return await cli.GetDiffAsync(diskPath, pr.TargetBranch, pr.SourceBranch, ct).ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    public string ResolveDiskPath(int projectId, string slug)
        => GitRepoPathResolver.TryResolve(_options.RepositoriesPath, projectId, slug)
           ?? throw new BadRequestException("Invalid repository path.");

    public string BuildCloneUrl(int projectId, string slug) =>
        MirrorCloneUrl.Build(configuration, httpContextAccessor.HttpContext, projectId, slug);

    // ── Blame ────────────────────────────────────────────────────────

    public async Task<List<GitLightBlameLine>> GetBlameAsync(int repoId, string refName, string path, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return [];

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        return await cli.GetBlameAsync(diskPath, refName, path, ct).ConfigureAwait(false);
    }

    // ── Commit Graph ───────────────────────────────────────────────

    public async Task<string> GetCommitGraphAsync(int repoId, int maxCount, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        if (entity is null) return string.Empty;

        var diskPath = ResolveDiskPath(entity.ProjectId, entity.Slug);
        maxCount = Math.Clamp(maxCount, 1, 500);
        return await cli.GetCommitGraphAsync(diskPath, maxCount, ct).ConfigureAwait(false);
    }

    public async Task<int?> GetProjectIdForRepoAsync(int repoId, CancellationToken ct = default)
    {
        var entity = await lightRepo.FindByIdAsync(repoId, ct).ConfigureAwait(false);
        return entity?.ProjectId;
    }

    // ── Branch Protection ───────────────────────────────────────────

    public Task<List<BranchProtectionRuleDto>> GetBranchProtectionRulesAsync(
        int repoId, CancellationToken ct = default) => branchProtection.GetRulesAsync(repoId, ct);

    public Task<BranchProtectionRuleDto> CreateBranchProtectionRuleAsync(
        int repoId, CreateBranchProtectionRuleRequest request, CancellationToken ct = default) =>
        branchProtection.CreateRuleAsync(repoId, request, ct);

    public Task<BranchProtectionRuleDto?> UpdateBranchProtectionRuleAsync(
        int repoId, int ruleId, UpdateBranchProtectionRuleRequest request,
        CancellationToken ct = default) => branchProtection.UpdateRuleAsync(repoId, ruleId, request, ct);

    public Task<bool> DeleteBranchProtectionRuleAsync(
        int repoId, int ruleId, CancellationToken ct = default) =>
        branchProtection.DeleteRuleAsync(repoId, ruleId, ct);

    public Task<bool> IsBranchProtectedAsync(
        int repoId, string branchName, CancellationToken ct = default) =>
        branchProtection.IsProtectedAsync(repoId, branchName, ct);
}
