// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Git;

/// <summary> The column header filters of the git repositories list. Each key is the grid column's
/// key ("isEmpty" backs the Status column, which reads Empty or Active). </summary>
internal static class GitRepositoryListQuery
{
    internal static readonly GridQueryMap<GitInternalRepo> Columns = new GridQueryMap<GitInternalRepo>()
        .Text("name", repository => repository.Name)
        .Text("projectName", repository => repository.Project.Name)
        .Text("description", repository => repository.Description)
        .Text("defaultBranch", repository => repository.DefaultBranch)
        .Boolean("isEmpty", repository => repository.IsEmpty)
        .Date("createdAt", repository => repository.CreatedAt)
        .Date("lastPushAt", repository => repository.LastPushAt);
}
