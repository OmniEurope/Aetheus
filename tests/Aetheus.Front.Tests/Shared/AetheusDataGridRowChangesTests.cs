// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Front.Components.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// R-10, the one mechanism for a row that changes. A page hands the grid what its action answered
/// (<c>ShowRowAsync</c>, <c>HideRowAsync</c>); the row appears, changes or goes before the server read that
/// confirms it ends, and that read neither empties nor veils the table. A read that fails keeps the rows drawn.
/// </summary>
public sealed class AetheusDataGridRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";

    public AetheusDataGridRowChangesTests() => BunitTestHelper.RegisterServices(this);

    private sealed record Row(int Id, string Name);

    /// <summary>A page that owns its rows: LoadData reads the "server" list into <see cref="Held"/>, VirtualData hands it back.</summary>
    private sealed class PageStub
    {
        public List<Row> Server { get; } = [new(1, "alpha"), new(2, "beta")];
        public List<Row> Held { get; private set; } = [];
        public int Reads { get; private set; }
        public TaskCompletionSource? Gate { get; set; }
        public Exception? FailNextWith { get; set; }

        public async Task LoadAsync(GridLoadArgs _)
        {
            Reads++;
            var gate = Gate;
            Gate = null;
            if (gate is not null) await gate.Task;
            if (FailNextWith is { } failure)
            {
                FailNextWith = null;
                throw failure;
            }
            Held = [.. Server];
        }

        public OmniDataGridResult<Row> Virtual() => new(Held, Held.Count);

        public TaskCompletionSource HoldNextRead() => Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private IRenderedComponent<AetheusDataGrid<Row>> RenderGrid(PageStub page)
    {
        var cut = Render<AetheusDataGrid<Row>>(parameters => parameters
            .Add(p => p.Data, page.Held).Add(p => p.Count, 2).Add(p => p.Virtualize, true)
            .Add(p => p.ExportMarkdown, false).Add(p => p.Columns, Columns)
            .Add(p => p.LoadData, page.LoadAsync).Add(p => p.VirtualData, page.Virtual));
        cut.WaitForAssertion(() => Assert.Contains("alpha", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private static int Occurrences(IRenderedComponent<AetheusDataGrid<Row>> cut, string text) =>
        Regex.Matches(cut.Markup, Regex.Escape(text)).Count;

    [Fact]
    public async Task AnAddedRow_ShowsBeforeTheServerReadEnds_WithoutBarOrEmptiedTable_AndTheReadConfirmsIt()
    {
        var page = new PageStub();
        var cut = RenderGrid(page);
        var gate = page.HoldNextRead();
        var created = new Row(3, "gamma");

        await cut.InvokeAsync(() => cut.Instance.ShowRowAsync(created));

        Assert.False(cut.Instance.BackgroundRefresh.IsCompleted);
        Assert.Contains("gamma", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("alpha", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("beta", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        page.Server.Add(created);
        gate.SetResult();
        await cut.InvokeAsync(() => cut.Instance.BackgroundRefresh);

        Assert.Equal(1, Occurrences(cut, "gamma"));
        Assert.Contains("alpha", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModifiedRow_ShowsTheAnswerOfTheAction_BeforeTheServerReadEnds()
    {
        var page = new PageStub();
        var cut = RenderGrid(page);
        var gate = page.HoldNextRead();

        await cut.InvokeAsync(() => cut.Instance.ShowRowAsync(new Row(2, "beta-renamed")));

        Assert.False(cut.Instance.BackgroundRefresh.IsCompleted);
        Assert.Contains("beta-renamed", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(0, Occurrences(cut, ">beta<"));
        Assert.Empty(cut.FindAll(Bar));

        page.Server[1] = new Row(2, "beta-renamed");
        gate.SetResult();
        await cut.InvokeAsync(() => cut.Instance.BackgroundRefresh);

        Assert.Equal(1, Occurrences(cut, "beta-renamed"));
    }

    [Fact]
    public async Task ADeletedRow_GoesBeforeTheServerReadEnds_AndStaysGoneAfterIt()
    {
        var page = new PageStub();
        var cut = RenderGrid(page);
        var gate = page.HoldNextRead();

        await cut.InvokeAsync(() => cut.Instance.HideRowAsync(new Row(1, "alpha")));

        Assert.False(cut.Instance.BackgroundRefresh.IsCompleted);
        Assert.DoesNotContain("alpha", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("beta", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        page.Server.RemoveAt(0);
        gate.SetResult();
        await cut.InvokeAsync(() => cut.Instance.BackgroundRefresh);

        Assert.DoesNotContain("alpha", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("beta", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowTheServerDidNotKeep_GoesBackToTheServersState_WhenTheReadEnds()
    {
        var page = new PageStub();
        var cut = RenderGrid(page);
        var gate = page.HoldNextRead();

        await cut.InvokeAsync(() => cut.Instance.ShowRowAsync(new Row(1, "alpha-edited")));
        Assert.Contains("alpha-edited", cut.Markup, StringComparison.Ordinal);

        // The server still holds "alpha": its read is the truth, no local edit survives it.
        gate.SetResult();
        await cut.InvokeAsync(() => cut.Instance.BackgroundRefresh);

        Assert.DoesNotContain("alpha-edited", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("alpha", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedAction_RestoreRowsDropsTheUnconfirmedChange_AndReadsTheRealRowsWithoutEmptyingTheTable()
    {
        var page = new PageStub();
        var cut = RenderGrid(page);
        var firstGate = page.HoldNextRead();
        await cut.InvokeAsync(() => cut.Instance.ShowRowAsync(new Row(1, "alpha-edited")));
        Assert.Contains("alpha-edited", cut.Markup, StringComparison.Ordinal);

        await cut.InvokeAsync(cut.Instance.RestoreRowsAsync);

        Assert.DoesNotContain("alpha-edited", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("alpha", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("beta", cut.Markup, StringComparison.Ordinal);
        firstGate.TrySetResult();
        await cut.InvokeAsync(() => cut.Instance.BackgroundRefresh);
        Assert.DoesNotContain("alpha-edited", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadThatFails_KeepsTheRowsDrawn_AndSaysSo()
    {
        var page = new PageStub();
        var cut = RenderGrid(page);
        page.FailNextWith = new HttpRequestException("read failed");

        await cut.InvokeAsync(() => cut.Instance.ShowRowAsync(new Row(3, "gamma")));
        await cut.InvokeAsync(() => cut.Instance.BackgroundRefresh);

        Assert.Contains("gamma", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("alpha", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll(".grid-load-alert"));
    }

    [Fact]
    public async Task TheReadBehindAChange_DrawsNoBar_EvenWhenThePageRaisesItsLoadingFlag()
    {
        var page = new PageStub();
        var cut = RenderGrid(page);
        var gate = page.HoldNextRead();

        await cut.InvokeAsync(() => cut.Instance.ShowRowAsync(new Row(3, "gamma")));
        // A page raises its own flag when its LoadData runs; the quiet read must not turn it into a bar.
        cut.Render(parameters => parameters.Add(p => p.IsLoading, true));

        Assert.False(cut.FindComponent<OmniDataGrid<Row>>().Instance.Busy);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult();
        await cut.InvokeAsync(() => cut.Instance.BackgroundRefresh);
        cut.Render(parameters => parameters.Add(p => p.IsLoading, false));
        Assert.False(cut.FindComponent<OmniDataGrid<Row>>().Instance.Busy);
    }

    [Fact]
    public async Task AGridFedByData_RefusesARowChange_InsteadOfPretendingToDrawIt()
    {
        var rows = new List<Row> { new(1, "alpha") };
        var cut = Render<AetheusDataGrid<Row>>(parameters => parameters
            .Add(p => p.Data, rows).Add(p => p.Count, rows.Count).Add(p => p.ExportMarkdown, false).Add(p => p.Columns, Columns));
        cut.WaitForAssertion(() => Assert.Contains("alpha", cut.Markup, StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cut.InvokeAsync(() => cut.Instance.ShowRowAsync(new Row(3, "gamma"))));
    }

    private static readonly RenderFragment Columns = builder =>
    {
        builder.OpenComponent<OmniDataGridColumn<Row>>(0);
        builder.AddComponentParameter(1, nameof(OmniDataGridColumn<Row>.Property), nameof(Row.Name));
        builder.AddComponentParameter(2, nameof(OmniDataGridColumn<Row>.Title), "Name");
        builder.CloseComponent();
    };
}
