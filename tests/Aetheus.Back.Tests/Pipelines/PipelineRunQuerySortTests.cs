// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The runs grid's server-side order: every legacy SortBy key in both directions, the
/// duration of a run still going measured against "now", the grid's sort keys taking precedence,
/// and the newest-first fallback for a missing or unknown key.</summary>
public sealed class PipelineRunQuerySortTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = T0.AddHours(3).AddMinutes(30);

    private static PipelineRun[] Runs() =>
    [
        new()
        {
            Id = 1, Status = PipelineStatus.Success, StartedAt = T0.AddHours(1),
            CompletedAt = T0.AddHours(1).AddMinutes(10), BranchName = "main", CommitHash = "bbb"
        },
        // Still running: its duration is 30 minutes against Now.
        new()
        {
            Id = 2, Status = PipelineStatus.Failed, StartedAt = T0.AddHours(3),
            CompletedAt = null, BranchName = "dev", CommitHash = "aaa"
        },
        new()
        {
            Id = 3, Status = PipelineStatus.Pending, StartedAt = T0.AddHours(2),
            CompletedAt = T0.AddHours(2).AddMinutes(5), BranchName = "feature", CommitHash = "ccc"
        },
    ];

    [Theory]
    [InlineData("id", false, new[] { 1, 2, 3 })]
    [InlineData("ID", true, new[] { 3, 2, 1 })]
    [InlineData("status", false, new[] { 3, 1, 2 })]
    [InlineData("status", true, new[] { 2, 1, 3 })]
    [InlineData("startedAt", false, new[] { 1, 3, 2 })]
    [InlineData("startedAt", true, new[] { 2, 3, 1 })]
    [InlineData("completedAt", false, new[] { 2, 1, 3 })]
    [InlineData("completedAt", true, new[] { 3, 1, 2 })]
    [InlineData("branchName", false, new[] { 2, 3, 1 })]
    [InlineData("branchName", true, new[] { 1, 3, 2 })]
    [InlineData("commitHash", false, new[] { 2, 1, 3 })]
    [InlineData("commitHash", true, new[] { 3, 1, 2 })]
    [InlineData("duration", false, new[] { 3, 1, 2 })]
    [InlineData("duration", true, new[] { 2, 1, 3 })]
    [InlineData("nonsense", false, new[] { 2, 3, 1 })]
    [InlineData(null, false, new[] { 2, 3, 1 })]
    public void ApplySort_OrdersOnTheRequestedKey(string? sortBy, bool descending, int[] expectedIds)
    {
        var request = new PipelineRunPaginationRequest { SortBy = sortBy, SortDescending = descending };

        var sorted = PipelineRunQuery.ApplySort(Runs().AsQueryable(), request, Now);

        Assert.Equal(expectedIds, sorted.Select(run => run.Id).ToArray());
    }

    [Fact]
    public void ApplySort_WithoutRequest_ListsNewestFirst()
    {
        var sorted = PipelineRunQuery.ApplySort(Runs().AsQueryable(), request: null, Now);

        Assert.Equal([2, 3, 1], sorted.Select(run => run.Id).ToArray());
    }

    [Fact]
    public void ApplySort_GridSortKeysReplaceTheLegacySortBy()
    {
        var request = new PipelineRunPaginationRequest
        {
            SortBy = "id",
            Sorts = [new GridSort { Field = "branchName", Descending = true }]
        };

        var sorted = PipelineRunQuery.ApplySort(Runs().AsQueryable(), request, Now);

        Assert.Equal([1, 3, 2], sorted.Select(run => run.Id).ToArray());
    }
}
