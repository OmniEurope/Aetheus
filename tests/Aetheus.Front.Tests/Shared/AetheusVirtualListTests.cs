// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Virtual-list tests cover windowing, scroll-position changes and CSSOM spacer sizing
/// without inline style attributes under the strict CSP.</summary>
public class AetheusVirtualListTests : BunitContext
{
    public AetheusVirtualListTests() => BunitTestHelper.RegisterServices(this);

    private const double RowHeight = 20;
    private const int Overscan = 5;

    private static List<string> Lines(int count) =>
        Enumerable.Range(0, count).Select(i => $"line {i}").ToList();

    private IRenderedComponent<AetheusVirtualList<string>> RenderList(int count)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        return Render<AetheusVirtualList<string>>(parameters => parameters
            .Add(p => p.Items, Lines(count))
            .Add(p => p.ItemSize, RowHeight)
            .Add(p => p.OverscanCount, Overscan)
            .Add(p => p.ChildContent, (RenderFragment<string>)(line => builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "vlist-row");
                builder.AddContent(2, line);
                builder.CloseElement();
            })));
    }

    [Fact]
    public void LongList_RendersAWindowInsteadOfEveryRow()
    {
        var cut = RenderList(1000);

        var rendered = cut.FindAll(".vlist-row").Count;
        Assert.True(rendered > 0, "the list must render something before the first measurement");
        Assert.True(rendered < 1000, $"{rendered} rows rendered out of 1000: the list is not windowing");
    }

    [Fact]
    public async Task ScrollingTheViewport_MovesTheWindowAndReservesTheSpaceAbove()
    {
        var cut = RenderList(1000);

        // 200 rows scrolled past, a 100px viewport: the window starts at 200 minus the overscan.
        await cut.InvokeAsync(() => cut.Instance.OnViewportAsync(200 * RowHeight, 100));

        Assert.Equal(200 - Overscan, cut.Instance.Start);
        Assert.Equal((100 / (int)RowHeight) + (2 * Overscan), cut.Instance.Count);
        Assert.Contains("line 200", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(">line 0<", cut.Markup, StringComparison.Ordinal);

        // The spacers carry no style attribute: their height arrives through the CSSOM.
        foreach (var spacer in cut.FindAll(".aetheus-vlist-spacer"))
            Assert.Null(spacer.GetAttribute("style"));
    }

    [Fact]
    public void ShortList_RendersEveryRow()
    {
        var cut = RenderList(3);

        Assert.Equal(3, cut.FindAll(".vlist-row").Count);
        Assert.Equal(0, cut.Instance.Start);
    }

    [Fact]
    public void EmptyList_RendersNoRowAndNoWindow()
    {
        var cut = RenderList(0);

        Assert.Empty(cut.FindAll(".vlist-row"));
        Assert.Equal(0, cut.Instance.Count);
    }
}
