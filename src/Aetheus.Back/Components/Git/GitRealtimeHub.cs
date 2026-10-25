// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Git;

/// <summary>
/// Realtime updates for the Git module: branches, tags, commits, pull requests.
/// Clients join a group keyed by repository id; the server pushes events from
/// post-receive / pull-request operations so the UI updates without a page refresh.
/// </summary>
[Authorize]
public class GitRealtimeHub(IResourceAuthorizationService authz, IGitLightRepository gitRepo) : Hub
{
    public async Task JoinRepositoryGroup(int repositoryId)
    {
        // Resolve the repository's owning project and authorize Read on that specific project,
        // instead of granting access to anyone with Read on any project.
        var repo = await gitRepo.FindByIdAsync(repositoryId).ConfigureAwait(false);
        if (repo is null ||
            !await authz.HasPermissionAsync(Context.User!, ResourceType.Project, repo.ProjectId, Permission.Read).ConfigureAwait(false))
            throw new HubException("Access denied.");

        await Groups.AddToGroupAsync(Context.ConnectionId, GitRealtimeGroups.Repository(repositoryId)).ConfigureAwait(false);
    }

    public Task LeaveRepositoryGroup(int repositoryId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GitRealtimeGroups.Repository(repositoryId));

}

public static class GitRealtimeGroups
{
    public static string Repository(int repositoryId) => $"git-repo-{repositoryId}";
}

public static class GitRealtimeEvents
{
    public const string BranchesChanged = "BranchesChanged";
    public const string TagsChanged = "TagsChanged";
    public const string CommitsChanged = "CommitsChanged";
    public const string PullRequestsChanged = "PullRequestsChanged";
    public const string RepositoryChanged = "RepositoryChanged";
}
