// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.AspNetCore.Components;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// A remote-virtualized grid must hand <c>OmniDataGrid</c> an equal <c>Load</c> delegate on every
/// render. When the loader differs, the library drops the virtual window it had already fetched; with
/// OmniEurope.Blazor 1.1.0 it compared by reference, so a method group converted at render time (a fresh
/// delegate each time) invalidated the window on every render, fetched it again and rendered again: an
/// unbounded request loop that saturated the API rate limiter and froze the browser on the fourteen
/// pages built this way (reproduced on /servers, 8 000 requests in seconds). OE 1.2.0 compares by
/// delegate equality (same method, same target), which is what the wrapper now relies on: the loader
/// stays equal across renders and the window is fetched once.
/// </summary>
public class AetheusDataGridLoaderIdentityTests : BunitContext
{
    public AetheusDataGridLoaderIdentityTests() => BunitTestHelper.RegisterServices(this);

    private sealed record Row(int Id, string Name);

    [Fact]
    public void RemoteVirtualizedGrid_KeepsAnEqualLoaderAcrossRenders()
    {
        var rows = new List<Row> { new(1, "alpha"), new(2, "beta") };
        var cut = RenderGrid(rows);

        var grid = cut.FindComponent<OmniDataGrid<Row>>();
        var first = grid.Instance.Load;
        Assert.NotNull(first);

        // A re-render with changed parameters is exactly what the page does after each load, when it
        // raises StateHasChanged to publish the rows it just received.
        cut.Render(parameters => parameters.Add(p => p.Count, rows.Count + 1));

        var second = cut.FindComponent<OmniDataGrid<Row>>().Instance.Load;
        // The comparison OmniDataGrid itself makes (Equals on the delegate): same method, same target.
        Assert.True(Equals(first, second), "The wrapper handed the grid a loader that is not equal to the previous one.");
    }

    [Fact]
    public void RemoteVirtualizedGrid_LoadsItsWindowOnce()
    {
        var rows = new List<Row> { new(1, "alpha"), new(2, "beta") };
        var loads = 0;
        var cut = RenderGrid(rows, () => loads++);

        cut.WaitForAssertion(() => Assert.True(loads > 0));

        var afterFirstRender = loads;
        cut.Render(parameters => parameters.Add(p => p.Count, rows.Count));

        // Without a stable loader this is where the count ran away: each render reset the window and
        // asked for it again.
        Assert.Equal(afterFirstRender, loads);
    }

    [Fact]
    public async Task RemoteVirtualizedGrid_DoesNotCatchUpDuringItsOwnLoad()
    {
        var rows = new List<Row> { new(1, "alpha"), new(2, "beta") };
        var loads = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderGrid(rows, () => loads++);
        cut.WaitForAssertion(() => Assert.True(loads > 0));
        var before = loads;
        cut.Render(p => p.Add(x => x.LoadData, async _ =>
        {
            loads++;
            rows.RemoveAt(0);
            await gate.Task;
        }));
        var grid = cut.FindComponent<OmniDataGrid<Row>>().Instance;
        var loading = cut.InvokeAsync(() => grid.RefreshAsync());
        cut.WaitForAssertion(() => Assert.Equal(before + 1, loads));
        try
        {
            cut.Render(p => p.Add(x => x.Count, rows.Count));
            Assert.Equal(before + 1, loads);
        }
        finally
        {
            gate.TrySetResult();
            await loading;
        }
    }

    private IRenderedComponent<AetheusDataGrid<Row>> RenderGrid(List<Row> rows, Action? onLoad = null) =>
        Render<AetheusDataGrid<Row>>(parameters => parameters
            .Add(p => p.Data, rows)
            .Add(p => p.Count, rows.Count)
            .Add(p => p.Virtualize, true)
            .Add(p => p.LoadData, _ => { onLoad?.Invoke(); return Task.CompletedTask; })
            // Loader tests omit export behaviour, which has dedicated coverage.
            .Add(p => p.ExportMarkdown, false)
            .Add(p => p.VirtualData, () => new OmniDataGridResult<Row>(rows, rows.Count))
            .Add(p => p.Columns, BuildColumns(rows)));

    private static RenderFragment BuildColumns(List<Row> rows) => builder =>
    {
        builder.OpenComponent<OmniDataGridColumn<Row>>(0);
        builder.AddComponentParameter(1, nameof(OmniDataGridColumn<Row>.Property), nameof(Row.Name));
        builder.AddComponentParameter(2, nameof(OmniDataGridColumn<Row>.Title), "Name");
        builder.CloseComponent();
    };
}
