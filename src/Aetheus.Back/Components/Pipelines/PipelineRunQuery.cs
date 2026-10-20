// SPDX-License-Identifier: EUPL-1.2
using System.Linq.Expressions;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Filtering and ordering for a page of pipeline runs.
/// <para>Both are applied server-side because the runs grid is server-paged. Sorting or filtering the
/// loaded page instead would order 25 rows and present it as the order of the history, which is a
/// working-looking affordance that tells the reader something false.</para></summary>
internal static class PipelineRunQuery
{
    /// <summary>Allowed column keys for run-grid filtering and sorting.</summary>
    internal static readonly GridQueryMap<PipelineRun> Columns = new GridQueryMap<PipelineRun>()
        .Number("id", r => r.Id)
        .Number("buildNumber", r => r.BuildNumber)
        .Enum("status", r => r.Status)
        .Date("startedAt", r => r.StartedAt)
        .Date("completedAt", r => r.CompletedAt)
        .Text("branchName", r => r.BranchName)
        .Text("commitHash", r => r.CommitHash);

    /// <summary>Column filters, applied before the count so the pager reports the size of the filtered
    /// set rather than of rows the reader can never reach.</summary>
    public static IQueryable<PipelineRun> ApplyFilters(
        IQueryable<PipelineRun> query, PipelineRunPaginationRequest? request)
    {
        if (request?.Status is { } status) query = query.Where(r => r.Status == status);

        // ToLower().Contains rather than ILike: it means the same thing to Npgsql (it translates to a
        // case-insensitive LIKE) and it is the only one of the two the in-memory provider the tests
        // run on can translate at all.
        if (!string.IsNullOrWhiteSpace(request?.BranchName))
        {
            var branch = request.BranchName.ToLowerInvariant();
            query = query.Where(r => r.BranchName != null && r.BranchName.ToLower().Contains(branch));
        }

        if (!string.IsNullOrWhiteSpace(request?.CommitHash))
        {
            var commit = request.CommitHash.ToLowerInvariant();
            query = query.Where(r => r.CommitHash != null && r.CommitHash.ToLower().Contains(commit));
        }

        // The generic column filters of the grid, on top of the typed ones an older front still sends.
        return Columns.ApplyFilters(query, request?.Filters);
    }

    /// <summary>Applies the requested order. The sort key is matched against a closed set of columns
    /// rather than interpolated into the query, so an unknown or hostile key falls back to the default
    /// order instead of reaching the database.</summary>
    /// <param name="now">Read from the injected TimeProvider and passed in, so it reaches SQL as one
    /// parameter: a duration order must not shift between the count and the page it describes.</param>
    public static IQueryable<PipelineRun> ApplySort(
        IQueryable<PipelineRun> query, PipelineRunPaginationRequest? request, DateTime now)
    {
        // Sort keys of the grid, when it sends them, replace SortBy. The newest run stays the tie-breaker
        // so rows with the same key keep a stable order across pages.
        if (Columns.ApplySorts(query, request?.Sorts) is { } sorted)
            return sorted.ThenByDescending(r => r.StartedAt).ThenByDescending(r => r.Id);

        var down = request?.SortDescending ?? true;

        return request?.SortBy?.ToLowerInvariant() switch
        {
            "id" => query.Ordered(r => r.Id, down),
            "status" => query.Ordered(r => r.Status, down),
            "startedat" => query.Ordered(r => r.StartedAt, down),
            "completedat" => query.Ordered(r => r.CompletedAt, down),
            "branchname" => query.Ordered(r => r.BranchName, down),
            "commithash" => query.Ordered(r => r.CommitHash, down),
            // Duration is not stored: it is what the column shows, so it is what gets sorted. A run
            // still going is measured against now, which places a long-running run where the reader
            // sees it instead of in a null bucket at the end.
            "duration" => query.Ordered(r => (r.CompletedAt ?? now) - r.StartedAt, down),
            // No key, or one this method does not recognise: newest first. The requested direction is
            // deliberately dropped with the key. Honouring it alone would let an unknown column silently
            // flip the list to oldest-first, which reads as a sort the reader never asked for.
            _ => query.OrderByDescending(r => r.StartedAt)
        };
    }

    /// <summary>Orders by one key in the given direction. Keeping the direction here rather than in a
    /// ternary per case is what keeps the switch above readable: with both, every column costs two
    /// branches and the method drifts past the repository's complexity budget.</summary>
    private static IQueryable<T> Ordered<T, TKey>(
        this IQueryable<T> query, Expression<Func<T, TKey>> key, bool descending)
        => descending ? query.OrderByDescending(key) : query.OrderBy(key);
}
