// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.AspNetCore.Components;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

/// <summary> A server-virtualized grid shows OE's loading bar between its headers and its first row
/// while a request of the grid runs. Every server grid of Aetheus goes through <see
/// cref="AetheusDataGrid{TItem}"/>, so the wrapper is where it is proved. </summary>
public sealed class AetheusDataGridLoadingBarTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";

    public AetheusDataGridLoadingBarTests() => BunitTestHelper.RegisterServices(this);

    private sealed record Row(int Id, string Name);

    [Fact]
    public async Task ARequestOfTheGrid_ShowsTheBarUntilItIsAnswered()
    {
        var rows = new List<Row> { new(1, "alpha"), new(2, "beta") };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render<AetheusDataGrid<Row>>(parameters => parameters
            .Add(p => p.Data, rows)
            .Add(p => p.Count, rows.Count)
            .Add(p => p.Virtualize, true)
            .Add(p => p.LoadData, async _ => await gate.Task)
            .Add(p => p.ExportMarkdown, false) // the export is AetheusDataGridExportTests' subject
            .Add(p => p.VirtualData, () => new OmniDataGridResult<Row>(rows, rows.Count))
            .Add(p => p.Columns, Columns));

        Assert.True(cut.FindComponent<OmniDataGrid<Row>>().Instance.ShowLoadingBar);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(Bar)));

        gate.SetResult();
        await Task.Yield();
        // Loaded-machine ceiling: the answer's continuation comes back through the thread pool; under load
        // the failures left here had run 0 or 1 check and passed 20 to 32 ms later (PLAN-002 B7).
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(Bar)));
    }

    /// <summary>Page-owned busy state reaches the grid through Busy and uses the same loading bar
    /// while retaining rows.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APageLoad_ShowsTheBarOnTheTable_WithoutAVeil(bool remote)
    {
        var rows = new List<Row> { new(1, "alpha"), new(2, "beta") };
        var cut = Render<AetheusDataGrid<Row>>(parameters =>
        {
            parameters.Add(p => p.Data, rows).Add(p => p.Count, rows.Count).Add(p => p.Columns, Columns);
            if (remote)
            {
                parameters.Add(p => p.LoadData, _ => Task.CompletedTask)
                    .Add(p => p.ExportMarkdown, false)
                    .Add(p => p.VirtualData, () => new OmniDataGridResult<Row>(rows, rows.Count));
            }
        });
        cut.WaitForAssertion(() => Assert.Contains("alpha", cut.Markup, StringComparison.Ordinal));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(Bar)));

        cut.Render(parameters => parameters.Add(p => p.IsLoading, true));

        Assert.True(cut.FindComponent<OmniDataGrid<Row>>().Instance.Busy);
        Assert.Single(cut.FindAll(Bar));
        Assert.Contains("alpha", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".aetheus-grid-loading"));

        cut.Render(parameters => parameters.Add(p => p.IsLoading, false));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(Bar)));
    }

    private static readonly RenderFragment Columns = builder =>
    {
        builder.OpenComponent<OmniDataGridColumn<Row>>(0);
        builder.AddComponentParameter(1, nameof(OmniDataGridColumn<Row>.Property), nameof(Row.Name));
        builder.AddComponentParameter(2, nameof(OmniDataGridColumn<Row>.Title), "Name");
        builder.CloseComponent();
    };
}
