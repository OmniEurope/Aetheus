// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Shared;

namespace Aetheus.Back.Components.Git;

/// <summary>Maps approved commit column filters to git arguments: message contains, author and
/// branch selections, and committer date ranges. Reject unknown columns, operators and
/// shapes.</summary>
internal static class GitCommitListQuery
{
    internal const string MessageKey = "Message";
    internal const string AuthorKey = "AuthorName";
    internal const string BranchKey = "SourceRef";
    internal const string DateKey = "CommitDate";

    /// <summary>The filter git applies, or null when the grid sent none.</summary>
    internal static GitCommitLogFilter? Parse(IReadOnlyList<GridFilter>? filters)
    {
        if (filters is null || filters.Count == 0) return null;
        if (filters.Count > PaginationRequest.MaxFilters)
            throw new BadRequestException($"At most {PaginationRequest.MaxFilters} column filters are accepted.");

        var result = new GitCommitLogFilter();
        foreach (var filter in filters)
        {
            result = filter.Field switch
            {
                _ when Is(filter, MessageKey) => result with { Message = Message(filter, result.Message) },
                _ when Is(filter, AuthorKey) => result with { Authors = Intersect(result.Authors, List(filter)) },
                _ when Is(filter, BranchKey) => result with { Branches = Intersect(result.Branches, Branches(filter)) },
                _ when Is(filter, DateKey) => Dates(filter, result),
                _ => throw new BadRequestException($"'{filter.Field}' is not a column this grid can filter on.")
            };
        }

        return result;
    }

    private static bool Is(GridFilter filter, string key) =>
        string.Equals(filter.Field, key, StringComparison.OrdinalIgnoreCase);

    private static string Message(GridFilter filter, string? previous)
    {
        if (filter.Operator != GridFilterOperator.Contains || filter.SecondOperator is not null)
            throw new BadRequestException($"'{filter.Field}' only accepts a 'contains' text.");
        if (string.IsNullOrWhiteSpace(filter.Value) || filter.Value.Length > GridFilter.MaximumValueLength)
            throw new BadRequestException($"The filter on '{filter.Field}' needs a value of at most {GridFilter.MaximumValueLength} characters.");
        if (previous is not null)
            throw new BadRequestException($"'{filter.Field}' is filtered once.");
        return filter.Value;
    }

    private static IReadOnlyList<string> List(GridFilter filter)
    {
        if (filter.SecondOperator is not null)
            throw new BadRequestException($"'{filter.Field}' only accepts a list of values.");
        var values = filter.Operator switch
        {
            GridFilterOperator.In => GridQueryMap<object>.SplitList(filter.Value),
            GridFilterOperator.Equals when !string.IsNullOrWhiteSpace(filter.Value) => [filter.Value],
            _ => throw new BadRequestException($"'{filter.Field}' only accepts a list of values.")
        };
        if (values.Count == 0) throw new BadRequestException($"The filter on '{filter.Field}' needs a value.");
        return values;
    }

    // A branch name reaches git as `refs/heads/<name>`; refusing what git itself refuses as a branch name
    // (a leading '-', "..", "@{", ':', '^', '~', spaces...) keeps it from ever reading as an option or a
    // revision expression.
    private static IReadOnlyList<string> Branches(GridFilter filter)
    {
        var values = List(filter);
        var invalid = values.FirstOrDefault(value => !GitBranchNameValidator.IsValid(value));
        if (invalid is not null)
            throw new BadRequestException($"'{invalid}' is not a valid branch name.");
        return values;
    }

    // Two filters on one column that share no value would leave an empty list, which the log reader
    // reads as "no filter" and answers with every commit; refuse it instead (session audit 2026-10-01).
    private static IReadOnlyList<string> Intersect(IReadOnlyList<string>? previous, IReadOnlyList<string> values)
    {
        if (previous is null) return [.. values];
        IReadOnlyList<string> shared = [.. previous.Intersect(values, StringComparer.Ordinal)];
        return shared.Count > 0
            ? shared
            : throw new BadRequestException("Two filters on the same column share no value.");
    }

    // The grid's date range arrives as a lower bound (>=) and an upper one (<), possibly alone. git's
    // --since/--until are inclusive to the second, so a strict bound moves by one second.
    private static GitCommitLogFilter Dates(GridFilter filter, GitCommitLogFilter result)
    {
        if (filter.SecondOperator is not null && filter.Logic != GridFilterLogic.And)
            throw new BadRequestException($"'{filter.Field}' only accepts a date range.");
        result = Bound(filter.Field, filter.Operator, filter.Value, result);
        return filter.SecondOperator is { } second ? Bound(filter.Field, second, filter.SecondValue, result) : result;
    }

    private static GitCommitLogFilter Bound(string field, GridFilterOperator op, string? value, GitCommitLogFilter result)
    {
        if (string.IsNullOrWhiteSpace(value) || !DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
            throw new BadRequestException($"'{value}' is not a valid value for '{field}'.");
        return op switch
        {
            GridFilterOperator.GreaterThanOrEqual => result with { Since = Later(result.Since, date) },
            GridFilterOperator.GreaterThan => result with { Since = Later(result.Since, date.AddSeconds(1)) },
            GridFilterOperator.LessThanOrEqual => result with { Until = Earlier(result.Until, date) },
            GridFilterOperator.LessThan => result with { Until = Earlier(result.Until, date.AddSeconds(-1)) },
            _ => throw new BadRequestException($"'{field}' only accepts a date range.")
        };
    }

    private static DateTime Later(DateTime? current, DateTime candidate) =>
        current is { } value && value > candidate ? value : candidate;

    private static DateTime Earlier(DateTime? current, DateTime candidate) =>
        current is { } value && value < candidate ? value : candidate;
}

/// <summary>The column header filters of a repository's pull requests grid.</summary>
internal static class GitPullRequestListQuery
{
    internal static readonly GridQueryMap<Aetheus.Back.Data.Entities.PullRequest> Columns = new GridQueryMap<Aetheus.Back.Data.Entities.PullRequest>()
        .Number("number", pr => pr.ExternalId)
        .Text("title", pr => pr.Title)
        .Text("sourceBranch", pr => pr.SourceBranch)
        .Text("authorLogin", pr => pr.AuthorLogin)
        .Enum("status", pr => pr.Status)
        .Date("createdAt", pr => pr.ExternalCreatedAt);
}
