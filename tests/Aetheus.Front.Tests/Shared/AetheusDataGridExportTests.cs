// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Markdown export includes every filtered row through paged <see
/// cref="AetheusDataGrid{TItem}.ExportRead"/> requests, with header metadata derived from the
/// page.</summary>
public sealed class AetheusDataGridExportTests : BunitContext
{
    private const int Total = 250;

    public AetheusDataGridExportTests()
    {
        BunitTestHelper.RegisterServices(this);
        Services.GetRequiredService<BreadcrumbService>().Set(
            new BreadcrumbItem("Projects", "/projects"), new BreadcrumbItem("Aetheus", "/projects/7"), new BreadcrumbItem("Quality"));
    }

    private sealed record Row(int Id, string Name);

    private static List<Row> Rows(int first, int count) =>
        [.. Enumerable.Range(first, count).Select(id => new Row(id, $"row {id}"))];

    private BunitJSModuleInterop DownloadModule()
    {
        var module = JSInterop.SetupModule("./_content/OmniEurope.Blazor/omni-document-editor.js");
        module.SetupVoid("download", _ => true).SetVoidResult();
        return module;
    }

    private static (string FileName, string Content) Downloaded(BunitJSModuleInterop module)
    {
        var download = module.Invocations["download"].Single();
        return (Assert.IsType<string>(download.Arguments[0]),
            Encoding.UTF8.GetString(Assert.IsType<byte[]>(download.Arguments[2])).TrimStart('﻿'));
    }

    /// <summary>The page shows the first 20 of 250 rows; its reader serves the API's pages of 200.</summary>
    private (IRenderedComponent<AetheusDataGrid<Row>> Grid, List<GridLoadArgs> Reads, Func<int> PageLoads) RenderRemoteGrid(
        Func<GridLoadArgs, Task<PaginatedResult<Row>>>? reader = null, bool showFooterBar = true)
    {
        var shown = Rows(1, 20);
        var reads = new List<GridLoadArgs>();
        var pageLoads = 0;
        reader ??= args =>
        {
            reads.Add(args);
            var page = Rows((args.Skip ?? 0) + 1, Math.Min(args.Top ?? 20, Total - (args.Skip ?? 0)));
            return Task.FromResult(new PaginatedResult<Row> { Items = page, TotalCount = Total });
        };
        var cut = Render<AetheusDataGrid<Row>>(parameters => parameters
            .Add(p => p.Data, shown)
            .Add(p => p.Count, Total)
            .Add(p => p.LoadData, _ =>
            {
                pageLoads++;
                return Task.CompletedTask;
            })
            .Add(p => p.VirtualData, () => new OmniDataGridResult<Row>(shown, Total))
            .Add(p => p.ExportRead, reader)
            .Add(p => p.ShowFooterBar, showFooterBar)
            .Add(p => p.ExportName, "Findings")
            .Add(p => p.Columns, Columns));
        cut.WaitForAssertion(() => Assert.Contains("row 1", cut.Markup, StringComparison.Ordinal));
        return (cut, reads, () => pageLoads);
    }

    [Fact]
    public void R2124_AGridByDefault_HasNoExportBar()
    {
        var (cut, _, _) = RenderRemoteGrid(showFooterBar: false);

        Assert.Empty(cut.FindAll(".omni-data-grid__export"));
        Assert.Empty(cut.FindAll(".omni-data-grid__bar"));
        Assert.False(cut.FindComponent<OmniDataGrid<Row>>().Instance.ShowFooterBar);
    }

    [Fact]
    public void R2124_AGridThatOptsIntoMarkdown_HasOnlyMarkdown_Blue_UnderTheTable()
    {
        var (cut, _, _) = RenderRemoteGrid();

        var button = Assert.Single(cut.FindAll(".omni-data-grid__export-button"));
        Assert.Contains("omni-button--primary", button.ClassList);
        var grid = cut.FindComponent<OmniDataGrid<Row>>().Instance;
        Assert.True(grid.ShowFooterBar);
        Assert.False(grid.ExportCsv);
        Assert.False(grid.ExportExcel);
        Assert.False(grid.ExportPdf);
        // OE 1.6.0: the bar sits under the rows, inside the table's frame; Aetheus puts the button at its end (2026-10-06).
        Assert.Single(cut.FindAll(".omni-data-grid__frame .omni-data-grid__bar--footer .omni-data-grid__bar-content + .omni-data-grid__export .omni-data-grid__export-button"));
        Assert.Equal(OmniDataGridBarExport.End, grid.FooterBarExport);
        var markup = cut.Markup;
        Assert.True(markup.IndexOf("omni-data-grid__export", StringComparison.Ordinal) > markup.IndexOf("<table", StringComparison.Ordinal));
    }

    [Fact]
    public async Task R2124_TheMarkdownExport_ReadsEveryPageThroughTheReader_AndSaysWhereTheRowsComeFrom()
    {
        var module = DownloadModule();
        var (cut, reads, pageLoads) = RenderRemoteGrid();
        var loadsBefore = pageLoads();

        await cut.FindAll(".omni-data-grid__export-button")[0].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["download"]));
        var (fileName, markdown) = Downloaded(module);
        // Two pages of 200, through the page's reader; the page's own load is not asked again.
        Assert.Equal(new[] { 0, 200 }, reads.Select(read => read.Skip ?? 0));
        Assert.All(reads, read => Assert.Equal(200, read.Top));
        Assert.Equal(loadsBefore, pageLoads());
        Assert.Contains("Projects > Aetheus > Quality > Findings", markdown, StringComparison.Ordinal);
        Assert.Contains("GridExportSource", markdown, StringComparison.Ordinal);
        Assert.Contains("Aetheus", markdown, StringComparison.Ordinal);
        Assert.Contains("GridExportAddress", markdown, StringComparison.Ordinal);
        Assert.Contains("| row 1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| row 250 |", markdown, StringComparison.Ordinal);
        Assert.Equal(Total, markdown.Split('\n').Count(line => line.StartsWith("| row ", StringComparison.Ordinal)));
        Assert.DoesNotContain("\n> ", markdown, StringComparison.Ordinal);
        // The breadcrumb past its module, then the table: OE appends its time and the extension.
        Assert.Matches(@"^aetheus-quality-findings-\d{4}-\d{2}-\d{2}-\d{4}\.md$", fileName);
        // The rows on screen are still the page's.
        Assert.Contains("row 20", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("row 21<", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R2124_APageThatCannotBeRead_WritesNoFile_AndSaysSo()
    {
        var module = DownloadModule();
        var (cut, _, _) = RenderRemoteGrid(args => (args.Skip ?? 0) == 0
            ? Task.FromResult(new PaginatedResult<Row> { Items = Rows(1, 200), TotalCount = Total })
            : throw new HttpRequestException("page 2 failed"));

        await cut.FindAll(".omni-data-grid__export-button")[0].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains(Services.Toasts(), toast => toast.Summary == "GridExportFailed"));
        Assert.Empty(module.Invocations["download"]);
    }

    [Fact]
    public void R2124_ALoadDataGridWithoutAReader_FailsLoudly()
    {
        var rows = Rows(1, 2);
        var failure = Assert.Throws<InvalidOperationException>(() => Render<AetheusDataGrid<Row>>(parameters => parameters
            .Add(p => p.Data, rows)
            .Add(p => p.LoadData, _ => Task.CompletedTask)
            .Add(p => p.VirtualData, () => new OmniDataGridResult<Row>(rows, rows.Count))
            .Add(p => p.ShowFooterBar, true)
            .Add(p => p.Columns, Columns)));

        Assert.Contains("ExportRead", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void R2124_AnOptedOutGrid_ShowsNoBar_AndNeedsNoReader_EvenWhenAskedForMarkdown()
    {
        var rows = Rows(1, 2);
        var cut = Render<AetheusDataGrid<Row>>(parameters => parameters
            .Add(p => p.Data, rows)
            .Add(p => p.LoadData, _ => Task.CompletedTask)
            .Add(p => p.VirtualData, () => new OmniDataGridResult<Row>(rows, rows.Count))
            .Add(p => p.ExportMarkdown, false)
            .Add(p => p.ShowFooterBar, true)
            .Add(p => p.Columns, Columns));

        cut.WaitForAssertion(() => Assert.Contains("row 1", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".omni-data-grid__export-button"));
    }

    [Fact]
    public async Task R2124_ALocalGrid_ExportsEveryRowItHolds_NotThePageOnScreen()
    {
        var module = DownloadModule();
        var rows = Rows(1, 45);
        var cut = Render<AetheusDataGrid<Row>>(parameters => parameters
            .Add(p => p.Data, rows)
            .Add(p => p.PageSize, 20)
            .Add(p => p.ShowFooterBar, true)
            .Add(p => p.Columns, Columns));
        cut.WaitForAssertion(() => Assert.Contains("row 1", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("row 45<", cut.Markup, StringComparison.Ordinal);

        await cut.FindAll(".omni-data-grid__export-button")[0].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["download"]));
        var (fileName, markdown) = Downloaded(module);
        Assert.Equal(45, markdown.Split('\n').Count(line => line.StartsWith("| row ", StringComparison.Ordinal)));
        Assert.Contains("Projects > Aetheus > Quality", markdown, StringComparison.Ordinal);
        Assert.Matches(@"^aetheus-quality-\d{4}-\d{2}-\d{2}-\d{4}\.md$", fileName);
    }

    [Fact]
    public async Task R2124_ThePageTitleAndFields_WinOverTheDefaults()
    {
        var module = DownloadModule();
        var rows = Rows(1, 3);
        var cut = Render<AetheusDataGrid<Row>>(parameters => parameters
            .Add(p => p.Data, rows)
            .Add(p => p.ShowFooterBar, true)
            .Add(p => p.ExportTitle, "Own title")
            .Add(p => p.ExportFields, [new OmniTableExportField("Period", "last 24 hours")])
            .Add(p => p.ExportFileName, "own-name")
            .Add(p => p.Columns, Columns));
        cut.WaitForAssertion(() => Assert.Contains("row 1", cut.Markup, StringComparison.Ordinal));

        await cut.FindAll(".omni-data-grid__export-button")[0].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Single(module.Invocations["download"]));
        var (fileName, markdown) = Downloaded(module);
        Assert.Contains("Own title", markdown, StringComparison.Ordinal);
        Assert.Contains("last 24 hours", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Projects > Aetheus", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("GridExportAddress", markdown, StringComparison.Ordinal);
        Assert.StartsWith("own-name-", fileName, StringComparison.Ordinal);
    }

    private static readonly RenderFragment Columns = builder =>
    {
        builder.OpenComponent<OmniDataGridColumn<Row>>(0);
        builder.AddComponentParameter(1, nameof(OmniDataGridColumn<Row>.Property), nameof(Row.Name));
        builder.AddComponentParameter(2, nameof(OmniDataGridColumn<Row>.Title), "Name");
        builder.CloseComponent();
    };
}
