// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Git;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Helpers;

/// <summary>Reading one column's header filter back from the grid's load arguments, and the two small
/// state-to-text mappings of the git commit and server overview pages.</summary>
public sealed class GridColumnFilterReadingTests
{
    private static GridLoadArgs With(params GridFilterDescriptor[] filters) => new() { Filters = filters };

    [Fact]
    public void ColumnFilter_ReturnsTheTypedText_MatchingThePropertyCaseInsensitively()
    {
        var args = With(new GridFilterDescriptor("Name", "alpha", OmniDataGridFilterOperator.Contains));

        Assert.Equal("alpha", args.ColumnFilter("name"));
        Assert.Equal("alpha", args.ColumnFilter("NAME"));
    }

    [Fact]
    public void ColumnFilter_IsNull_ForAnotherColumn_ABlankValue_OrNoFilters()
    {
        var args = With(
            new GridFilterDescriptor("Name", "alpha", OmniDataGridFilterOperator.Contains),
            new GridFilterDescriptor("Owner", "   ", OmniDataGridFilterOperator.Contains),
            new GridFilterDescriptor("Empty", null, OmniDataGridFilterOperator.Contains));

        Assert.Null(args.ColumnFilter("Status"));
        Assert.Null(args.ColumnFilter("Owner"));
        Assert.Null(args.ColumnFilter("Empty"));
        Assert.Null(new GridLoadArgs().ColumnFilter("Name"));
    }

    [Fact]
    public void ColumnFilterOfAnEnum_ReadsAMemberName_IgnoringCase()
    {
        var args = With(new GridFilterDescriptor("Order", "descending", OmniDataGridFilterOperator.Equals));

        Assert.Equal(GridSortOrder.Descending, args.ColumnFilter<GridSortOrder>("order"));
    }

    [Fact]
    public void ColumnFilterOfAnEnum_KeepsAValueAlreadyOfTheEnumType()
    {
        var args = With(new GridFilterDescriptor("Order", GridSortOrder.Ascending, OmniDataGridFilterOperator.Equals));

        Assert.Equal(GridSortOrder.Ascending, args.ColumnFilter<GridSortOrder>("Order"));
    }

    [Fact]
    public void ColumnFilterOfAnEnum_IsNull_ForAnUnknownMember_AnotherColumn_ANullValue_OrNoFilters()
    {
        var args = With(
            new GridFilterDescriptor("Order", "sideways", OmniDataGridFilterOperator.Equals),
            new GridFilterDescriptor("Empty", null, OmniDataGridFilterOperator.Equals));

        Assert.Null(args.ColumnFilter<GridSortOrder>("Order"));
        Assert.Null(args.ColumnFilter<GridSortOrder>("Other"));
        Assert.Null(args.ColumnFilter<GridSortOrder>("Empty"));
        Assert.Null(new GridLoadArgs().ColumnFilter<GridSortOrder>("Order"));
    }

    [Fact]
    public void ColumnDateRange_ALowerBoundAlone_LeavesTheEndOpen_AsUtc()
    {
        var args = With(new GridFilterDescriptor("At", "2026-03-10", OmniDataGridFilterOperator.GreaterThanOrEquals));

        var (from, to) = args.ColumnDateRange("At");

        Assert.Equal(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(DateTimeKind.Utc, from!.Value.Kind);
        Assert.Null(to);
    }

    [Fact]
    public void ColumnDateRange_TheSecondConditionGivesTheExclusiveEnd()
    {
        var args = With(new GridFilterDescriptor("At", "2026-03-10", OmniDataGridFilterOperator.GreaterThanOrEquals,
            OmniDataGridFilterOperator.LessThan, "2026-03-12"));

        var (from, to) = args.ColumnDateRange("At");

        Assert.Equal(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), from);
        Assert.Equal(new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc), to);
    }

    [Fact]
    public void ColumnDateRange_AnUnreadableDate_OrAnotherOperator_LeavesThatSideOpen()
    {
        var unreadable = With(new GridFilterDescriptor("At", "not a date", OmniDataGridFilterOperator.GreaterThanOrEquals,
            OmniDataGridFilterOperator.LessThan, "2026-03-12"));
        var otherOperator = With(new GridFilterDescriptor("At", "2026-03-10", OmniDataGridFilterOperator.Contains));

        Assert.Equal((null, new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc)), unreadable.ColumnDateRange("At"));
        Assert.Equal(((DateTime?)null, (DateTime?)null), otherOperator.ColumnDateRange("At"));
        Assert.Equal(((DateTime?)null, (DateTime?)null), new GridLoadArgs().ColumnDateRange("At"));
        Assert.Equal(((DateTime?)null, (DateTime?)null), unreadable.ColumnDateRange("Other"));
    }

    [Theory]
    [InlineData("added", "git-file-status-icon diff-file-stat-add")]
    [InlineData("Deleted", "git-file-status-icon diff-file-stat-del")]
    [InlineData("renamed", "git-file-status-icon omni-u-text-accent")]
    [InlineData("COPIED", "git-file-status-icon omni-u-text-accent")]
    [InlineData("modified", "git-file-status-icon omni-u-text-muted")]
    public void StatusIconClass_ColoursTheFileStatus(string status, string expected) =>
        Assert.Equal(expected, GitCommitDetail.StatusIconClass(status));

    [Theory]
    [InlineData("scanner:degraded:zap", "unavailable")]
    [InlineData("scanner:UNAVAILABLE:zap", "unavailable")]
    [InlineData("scanner:available-on-demand:zap", "on-demand")]
    [InlineData("scanner:ready:zap", "ready")]
    [InlineData("", "ready")]
    public void ScannerStateOf_ReadsTheDiagnosticMarker(string diagnostic, string expected) =>
        Assert.Equal(expected, Aetheus.Front.Components.Servers.ServerDetailSections.ServerOverviewSection.ScannerStateOf(diagnostic));
}
