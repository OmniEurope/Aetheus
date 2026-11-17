// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Shared;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

/// <summary>R-10: what the row changes do to the rows a page holds, and when a read settles them.</summary>
public sealed class GridRowOverlayTests
{
    private sealed record Row(int Id, string Name);

    private static object? Key(Row row) => row.Id;

    private static OmniDataGridResult<Row> Held(params Row[] rows) => new(rows, rows.Length);

    [Fact]
    public void ANewRow_GoesOnTopOfTheFirstWindow_AndCountsInTheTotal()
    {
        var overlay = new GridRowOverlay<Row>();
        overlay.Show(3, new Row(3, "gamma"), null);

        var shown = overlay.Apply(Held(new Row(1, "alpha")), skip: 0, pageSize: 20, virtualized: false, Key);

        Assert.Equal([3, 1], shown.Items.Select(row => row.Id));
        Assert.Equal(2, shown.TotalCount);
    }

    [Fact]
    public void ANewRow_IsNotDrawnOnALaterWindow_WhereTheServerPlacesIt()
    {
        var overlay = new GridRowOverlay<Row>();
        overlay.Show(3, new Row(3, "gamma"), null);

        var shown = overlay.Apply(Held(new Row(21, "x")), skip: 20, pageSize: 20, virtualized: false, Key);

        Assert.Equal([21], shown.Items.Select(row => row.Id));
        Assert.Equal(1, shown.TotalCount);
    }

    [Fact]
    public void ANewRow_GoesWhereItsOrderPutsIt_AndAPageKeepsItsSize()
    {
        var overlay = new GridRowOverlay<Row>();
        overlay.Show(3, new Row(3, "b"), (left, right) => string.CompareOrdinal(left.Name, right.Name));

        var inside = overlay.Apply(Held(new Row(1, "a"), new Row(2, "c")), 0, 3, false, Key);
        var pastTheEnd = overlay.Apply(Held(new Row(1, "a"), new Row(2, "a2")), 0, 2, false, Key);

        Assert.Equal([1, 3, 2], inside.Items.Select(row => row.Id));
        Assert.Equal([1, 2], pastTheEnd.Items.Select(row => row.Id));
    }

    [Fact]
    public void AChangedRow_ReplacesTheOneOfTheSameKey_WithoutMovingTheTotal()
    {
        var overlay = new GridRowOverlay<Row>();
        overlay.Show(2, new Row(2, "beta-2"), null);

        var shown = overlay.Apply(Held(new Row(1, "alpha"), new Row(2, "beta")), 0, 20, false, Key);

        Assert.Equal(["alpha", "beta-2"], shown.Items.Select(row => row.Name));
        Assert.Equal(2, shown.TotalCount);
    }

    [Fact]
    public void ADeletedRow_GoesAndTheTotalFalls_ButOnlyWhenTheWindowHeldIt()
    {
        var overlay = new GridRowOverlay<Row>();
        overlay.Hide(2, new Row(2, "beta"));

        var present = overlay.Apply(Held(new Row(1, "alpha"), new Row(2, "beta")), 0, 20, false, Key);
        var absent = overlay.Apply(Held(new Row(1, "alpha")), 0, 20, false, Key);

        Assert.Equal([1], present.Items.Select(row => row.Id));
        Assert.Equal(1, present.TotalCount);
        Assert.Equal(1, absent.TotalCount);
    }

    [Fact]
    public void AReadStartedBeforeTheChange_DoesNotBringTheOldRowBack_AReadStartedAfterItSettlesIt()
    {
        var overlay = new GridRowOverlay<Row>();
        var readStartedBefore = overlay.Sequence;
        overlay.Hide(2, new Row(2, "beta"));
        var readStartedAfter = overlay.Sequence;

        overlay.Settle(readStartedBefore);
        var stale = overlay.Apply(Held(new Row(1, "alpha"), new Row(2, "beta")), 0, 20, false, Key);
        overlay.Settle(readStartedAfter);
        var confirmed = overlay.Apply(Held(new Row(1, "alpha"), new Row(2, "beta")), 0, 20, false, Key);

        Assert.Equal([1], stale.Items.Select(row => row.Id));
        Assert.Equal([1, 2], confirmed.Items.Select(row => row.Id));
    }

    [Fact]
    public void Clear_DropsEveryChange()
    {
        var overlay = new GridRowOverlay<Row>();
        overlay.Show(3, new Row(3, "gamma"), null);
        overlay.Hide(1, new Row(1, "alpha"));

        overlay.Clear();
        var shown = overlay.Apply(Held(new Row(1, "alpha")), 0, 20, false, Key);

        Assert.Equal([1], shown.Items.Select(row => row.Id));
    }
}
