// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Releases;

/// <summary> The column filters of the releases list (global, project and server scopes). The
/// stored columns go through the generic map; the source pipeline, which the list shows as the root
/// of the release run's trigger chain (not a column of the release), is resolved to release ids
/// first. </summary>
internal static class ReleaseListQuery
{
    internal const string SourcePipelineNameKey = "SourcePipelineName";

    internal static readonly GridQueryMap<Release> Columns = new GridQueryMap<Release>()
        .Text("version", r => r.Version)
        .Text("projectName", r => r.Project.Name)
        .Text("branchName", r => r.BranchName)
        .Enum("status", r => r.Status)
        .Enum("assuranceGrade", r => r.AssuranceGrade)
        .Date("publishedAt", r => r.PublishedAt);

    /// <summary>Whether the filter is one this class resolves itself rather than through <see cref="Columns"/>.</summary>
    internal static bool IsResolvedInMemory(GridFilter filter) =>
        string.Equals(filter.Field, SourcePipelineNameKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The ids of the releases whose source pipeline is one of the ticked names. Only a checkable list
    /// (or a single value) is accepted on this column, the only filter the grid offers it.
    /// </summary>
    internal static HashSet<int> WithSourcePipelineAmong(
        GridFilter filter, IEnumerable<(int ReleaseId, string? PipelineName)> releases)
    {
        var wanted = ListOf(filter).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. releases
            .Where(release => release.PipelineName is { } name && wanted.Contains(name))
            .Select(release => release.ReleaseId)];
    }

    private static IReadOnlyList<string> ListOf(GridFilter filter) => filter.Operator switch
    {
        GridFilterOperator.In => GridQueryMap<Release>.SplitList(filter.Value),
        GridFilterOperator.Equals when !string.IsNullOrWhiteSpace(filter.Value) => [filter.Value],
        _ => throw new BadRequestException($"'{filter.Field}' only accepts a list of values.")
    };
}

/// <summary>What the releases list's checkable filters read of one release.</summary>
public sealed record ReleaseFilterFact(int Id, string ProjectName, int? PipelineRunId);
