// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Shared;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Registered grid defaults apply unless explicitly overridden on the instance.</summary>
public sealed class AetheusGridPresetsTests : BunitContext
{
    public AetheusGridPresetsTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void RawGrid_WithoutSettings_TakesTheSharedOnes()
    {
        var grid = Render<OmniDataGrid<string>>(parameters => parameters.Add(g => g.Items, ["a"])).Instance;

        Assert.True(grid.AllowSorting);
        Assert.True(grid.Filterable);
        Assert.Equal(OmniDataGridFilterMode.Simple, grid.FilterMode);
        Assert.True(grid.ShowHeaderFilterMenu);
        Assert.True(grid.AllowColumnResize);
        Assert.True(grid.AllowAlternatingRows);
        Assert.Equal(OmniDensity.Compact, grid.Density);
        Assert.Equal(OmniDataGridHeaderWrap.Wrap, grid.HeaderWrap); // R-35: a title is never truncated
    }

    /// <summary>Raw grids expose Markdown export only when their footer bar is enabled, with
    /// instance opt-outs taking precedence.</summary>
    [Fact]
    public void RawGrid_HasNoExportBarByDefault_MarkdownOnlyWhenAsked_AndTheOptOutWins()
    {
        var grid = Render<OmniDataGrid<string>>(parameters => parameters.Add(g => g.Items, ["a"]));

        Assert.False(grid.Instance.ShowFooterBar);
        Assert.False(grid.Instance.ExportCsv);
        Assert.False(grid.Instance.ExportExcel);
        Assert.False(grid.Instance.ExportPdf);
        Assert.Empty(grid.FindAll(".omni-data-grid__export-button"));

        var markdown = Render<OmniDataGrid<string>>(parameters => parameters
            .Add(g => g.Items, ["a"])
            .Add(g => g.ShowFooterBar, true));
        Assert.Equal(OmniButtonVariant.Primary, markdown.Instance.ExportVariants![OmniTableExportFormat.Markdown]);
        Assert.Contains("omni-button--primary", Assert.Single(markdown.FindAll(".omni-data-grid__bar--footer .omni-data-grid__export-button")).ClassList);
        Assert.Equal(OmniDataGridBarExport.End, markdown.Instance.FooterBarExport);

        var optedOut = Render<OmniDataGrid<string>>(parameters => parameters
            .Add(g => g.Items, ["a"])
            .Add(g => g.ShowFooterBar, true)
            .Add(g => g.ExportMarkdown, false));
        Assert.Empty(optedOut.FindAll(".omni-data-grid__export-button"));
    }

    [Fact]
    public void RawGrid_WrittenSetting_WinsOverThePreset()
    {
        var grid = Render<OmniDataGrid<string>>(parameters => parameters
            .Add(g => g.Items, ["a"])
            .Add(g => g.Density, OmniDensity.Comfortable)).Instance;

        Assert.Equal(OmniDensity.Comfortable, grid.Density);
        Assert.True(grid.AllowSorting);
    }
}
