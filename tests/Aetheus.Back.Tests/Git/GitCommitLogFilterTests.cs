// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;

namespace Aetheus.Back.Tests.Git;

/// <summary>The commit-count cache key reads the same for the same filter whatever the order of its
/// authors and branches, and differs as soon as one criterion does.</summary>
public sealed class GitCommitLogFilterTests
{
    private const char Sep = GridFilter.ListSeparator;

    [Fact]
    public void AnEmptyFilter_IsFiveEmptyParts() =>
        Assert.Equal("||||", new GitCommitLogFilter().CacheText());

    [Fact]
    public void EveryCriterion_IsWrittenInAStableOrderAndFormat()
    {
        var filter = new GitCommitLogFilter
        {
            Message = "fix",
            Authors = ["zoe", "alice"],
            Branches = ["main", "develop"],
            Since = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            Until = new DateTime(2026, 10, 2, 12, 30, 0, DateTimeKind.Utc)
        };

        Assert.Equal(
            $"fix|alice{Sep}zoe|develop{Sep}main|2026-10-01T00:00:00.0000000Z|2026-10-02T12:30:00.0000000Z",
            filter.CacheText());
    }

    [Fact]
    public void TheOrderOfAuthorsAndBranches_DoesNotChangeTheKey()
    {
        var one = new GitCommitLogFilter { Authors = ["a", "b"], Branches = ["x", "y"] };
        var other = new GitCommitLogFilter { Authors = ["b", "a"], Branches = ["y", "x"] };

        Assert.Equal(one.CacheText(), other.CacheText());
        Assert.NotEqual(one.CacheText(), (one with { Message = "m" }).CacheText());
    }
}
