// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Git;

/// <summary> The values the git repositories list's checkable column filters offer, read across
/// every repository in the caller's scope (the grid is loaded page by page). </summary>
public sealed record GitRepositoryFilterValuesDto
{
    public List<string> DefaultBranches { get; init; } = [];
}

/// <summary>The values a repository page's checkable column filters offer (the commits grid's
/// Author and Branch columns, the pull requests grid's Author column).</summary>
public sealed record GitRepositoryDetailFilterValuesDto
{
    public List<string> CommitAuthors { get; init; } = [];

    /// <summary>The branches the commits grid's Branch column filter offers.</summary>
    public List<string> CommitBranches { get; init; } = [];

    public List<string> PullRequestAuthors { get; init; } = [];
}
