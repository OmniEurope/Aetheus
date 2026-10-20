// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using OmniEurope.Blazor.Components;
using RunStatus = Aetheus.Shared.Components.Pipelines.PipelineStatus;

namespace Aetheus.Front.Tests.Shared;

/// <summary>Local-grid header filters narrow rows, and live replacements highlight new entries
/// while initial loads establish the baseline.</summary>
public class LocalGridHeaderFilterTests : BunitContext
{
    private const string NewRowClass = "omni-data-grid__row--new";

    public LocalGridHeaderFilterTests() => BunitTestHelper.RegisterServices(this);

    /// <summary>The status column filters on the catalogue statuses of the summary chips.</summary>
    [Fact]
    public async Task CatalogStatusFilter_KeepsTheRowsMatchingAnyChosenStatus()
    {
        var cut = RenderCatalog();
        var grid = cut.FindComponent<OmniDataGrid<PipelineDependencyDto>>().Instance;

        await cut.InvokeAsync(() => grid.SetFiltersAsync(Filter(PipelineDependencyGrid.StatusColumn, "Failed")));
        cut.WaitForAssertion(() => Assert.Equal(["build"], RowNames(cut)));

        await cut.InvokeAsync(() => grid.SetFiltersAsync(Filter(PipelineDependencyGrid.StatusColumn, "NeverRun", "Current")));
        cut.WaitForAssertion(() => Assert.Equal(["deploy", "lint"], RowNames(cut)));
    }

    /// <summary>The model column filters on the model name its cell shows, the trigger on its
    /// enum.</summary>
    [Fact]
    public async Task TemplateAndTriggerFilters_KeepTheMatchingRows()
    {
        var cut = RenderCatalog();
        var grid = cut.FindComponent<OmniDataGrid<PipelineDependencyDto>>().Instance;

        await cut.InvokeAsync(() => grid.SetFiltersAsync(Filter(PipelineDependencyGrid.TemplateColumn, "deploy-model")));
        cut.WaitForAssertion(() => Assert.Equal(["deploy"], RowNames(cut)));

        await cut.InvokeAsync(() => grid.SetFiltersAsync(
            Filter(nameof(PipelineDependencyDto.TriggerType), nameof(PipelineTriggerType.Webhook)), replace: true));
        cut.WaitForAssertion(() => Assert.Equal(["build", "lint"], RowNames(cut)));
    }

    [Fact]
    public void CatalogStatusFilter_ListsTheChipStatusesByTheirLabel()
    {
        var cut = RenderCatalog();
        var column = cut.FindComponents<OmniDataGridColumn<PipelineDependencyDto>>()
            .Single(candidate => candidate.Instance.Key == PipelineDependencyGrid.StatusColumn).Instance;

        Assert.Equal(OmniDataGridColumnFilterType.MultiSelect, column.FilterType);
        Assert.Equal(
            ["Failed", "Running", "NeverRun", "Outdated", "Current", "OffCatalog"],
            column.FilterValues!.ToArray());
        Assert.Equal("PipelineNeverRun", column.FormatFilterValue!("NeverRun"));
        Assert.Equal(
            [PipelineCatalogStatus.Failed, PipelineCatalogStatus.Current],
            PipelineDependencyGrid.ParseStatuses(OmniDataGridFilterValues.Join(["Failed", "bogus", "Current"])));
    }

    /// <summary>A monitored application that appears in the next list reads bold.</summary>
    [Fact]
    public void MonitoredAppsStatusGrid_MarksOnlyTheApplicationThatAppeared()
    {
        var first = new MonitoredAppStatusDto { Id = 1, Name = "shop", ProjectId = 1, CurrentStatus = AppHealthStatus.Up };
        var cut = Render<MonitoredAppsStatusGrid>(parameters => parameters
            .Add(component => component.Applications, new List<MonitoredAppStatusDto> { first }));
        Assert.DoesNotContain(NewRowClass, cut.Markup, StringComparison.Ordinal);

        var added = new MonitoredAppStatusDto { Id = 2, Name = "blog", ProjectId = 1, CurrentStatus = AppHealthStatus.Down };
        cut.Render(parameters => parameters
            .Add(component => component.Applications, new List<MonitoredAppStatusDto> { first, added }));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("tr[data-omni-row-index]");
            Assert.Contains(NewRowClass, Row(rows, "blog").ClassName ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(NewRowClass, Row(rows, "shop").ClassName ?? string.Empty, StringComparison.Ordinal);
        });
    }

    /// <summary>A run that the host's next list brings reads bold; the runs already there do
    /// not.</summary>
    [Fact]
    public void PipelineRunsGrid_MarksOnlyTheRunThatAppeared()
    {
        var existing = new PipelineRunDto { Id = 10, PipelineId = 1, PipelineName = "ci", ProjectId = 1, Status = RunStatus.Success };
        var cut = Render<PipelineRunsGrid>(parameters => parameters
            .Add(component => component.Items, new List<PipelineRunDto> { existing }));
        Assert.DoesNotContain(NewRowClass, cut.Markup, StringComparison.Ordinal);

        var started = new PipelineRunDto { Id = 12, PipelineId = 1, PipelineName = "ci", ProjectId = 1, Status = RunStatus.Pending };
        cut.Render(parameters => parameters
            .Add(component => component.Items, new List<PipelineRunDto> { started, existing }));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("tr[data-omni-row-index]");
            Assert.Contains(NewRowClass, Row(rows, "#12").ClassName ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(NewRowClass, Row(rows, "#10").ClassName ?? string.Empty, StringComparison.Ordinal);
        });
    }

    private IRenderedComponent<PipelineDependencyGrid> RenderCatalog()
    {
        var items = new List<PipelineDependencyDto>
        {
            new()
            {
                Id = 1, Name = "build", TriggerType = PipelineTriggerType.Webhook,
                RecentRuns = [new PipelineRunSummaryDto { Id = 100, Status = RunStatus.Failed }]
            },
            new()
            {
                Id = 2, Name = "deploy", TriggerType = PipelineTriggerType.Manual,
                RecentRuns = [new PipelineRunSummaryDto { Id = 101, Status = RunStatus.Success }]
            },
            new() { Id = 3, Name = "lint", TriggerType = PipelineTriggerType.Webhook }
        };
        var fleet = new Dictionary<int, PipelineFleetItemDto>
        {
            [1] = new() { PipelineId = 1, TemplateId = 7, TemplateName = "build-model", Freshness = PipelineFleetFreshness.Outdated },
            [2] = new() { PipelineId = 2, TemplateId = 8, TemplateName = "deploy-model", Freshness = PipelineFleetFreshness.Current },
            [3] = new() { PipelineId = 3, Freshness = PipelineFleetFreshness.OffCatalog }
        };
        return Render<PipelineDependencyGrid>(parameters => parameters
            .Add(component => component.Items, items)
            .Add(component => component.FleetItems, fleet)
            .Add(component => component.FleetAvailable, true)
            .Add(component => component.PipelineHref, id => $"/pipelines/{id}")
            .Add(component => component.RunPipeline, (_, _) => Task.CompletedTask)
            .Add(component => component.SetFavorite, (_, _) => Task.CompletedTask));
    }

    private static Dictionary<string, string?> Filter(string key, params string[] values) =>
        new() { [key] = OmniDataGridFilterValues.Join(values) };

    private static string[] RowNames(IRenderedComponent<PipelineDependencyGrid> cut) =>
        [.. cut.FindAll("tr[data-omni-row-index] a.pipeline-name-link").Select(link => link.TextContent.Trim()).Order(StringComparer.Ordinal)];

    private static AngleSharp.Dom.IElement Row(IEnumerable<AngleSharp.Dom.IElement> rows, string text) =>
        rows.Single(row => row.TextContent.Contains(text, StringComparison.Ordinal));
}
