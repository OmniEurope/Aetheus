// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Git;

/// <summary>Carries optional git log filters already validated by <see cref="GitCommitListQuery"/>;
/// null fields impose no filter.</summary>
public sealed record GitCommitLogFilter
{
    /// <summary>Text the commit message must contain, case-insensitive (<c>--grep</c>, matched literally).</summary>
    public string? Message { get; init; }

    /// <summary>Exact author names, a commit matching any of them (<c>--author</c>).</summary>
    public IReadOnlyList<string>? Authors { get; init; }

    /// <summary>Branch names whose history is walked (<c>refs/heads/&lt;name&gt;</c>). Null or empty
    /// leaves the walk to the caller's ref.</summary>
    public IReadOnlyList<string>? Branches { get; init; }

    /// <summary>Oldest committer date kept, UTC, inclusive (<c>--since</c>).</summary>
    public DateTime? Since { get; init; }

    /// <summary>Newest committer date kept, UTC, inclusive (<c>--until</c>).</summary>
    public DateTime? Until { get; init; }

    /// <summary>A stable text for the commit-count cache key: the same filter always reads the same.</summary>
    internal string CacheText() => string.Join('|',
        Message,
        string.Join(GridFilter.ListSeparator, (Authors ?? []).Order(StringComparer.Ordinal)),
        string.Join(GridFilter.ListSeparator, (Branches ?? []).Order(StringComparer.Ordinal)),
        Since?.ToString("O", CultureInfo.InvariantCulture),
        Until?.ToString("O", CultureInfo.InvariantCulture));
}
