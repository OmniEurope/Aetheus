// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public class DashboardEditTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public DashboardEditTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private static DashboardDto MakeDashboard(int id = 1) => new()
    {
        Id = id,
        Name = "Main Dashboard",
        IsDefault = true,
        RowVersion = Guid.NewGuid(),
        Widgets =
        [
            new DashboardWidgetDto
            {
                Id = 1,
                WidgetType = DashboardWidgetType.ServerCount,
                Title = "Servers",
                Row = 0, Column = 0, Width = 1, Height = 1, IsVisible = true
            },
            new DashboardWidgetDto
            {
                Id = 2,
                WidgetType = DashboardWidgetType.PipelineActivity,
                Title = "Pipelines",
                Row = 1, Column = 0, Width = 1, Height = 1, IsVisible = true
            }
        ]
    };

    private IRenderedComponent<DashboardEdit> RenderDashboard(int id = 1)
    {
        _handler.SetJsonResponse($"api/dashboards/{id}", MakeDashboard(id));
        _handler.SetJsonResponse($"api/dashboards/{id}/update", MakeDashboard(id));
        var cut = Render<DashboardEdit>(p => p.Add(x => x.Id, id));
        cut.WaitForState(() => !cut.Markup.Contains("rz-spinner"), TimeSpan.FromSeconds(2));
        return cut;
    }

    [Fact]
    public void Renders_DashboardEditPage_WithData()
    {
        var cut = RenderDashboard();
        // OnInitializedAsync maps the loaded dashboard into _name and the _widgets editor list.
        var name = (string)typeof(DashboardEdit).GetField("_name", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var widgets = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal("Main Dashboard", name);
        Assert.Equal(2, widgets.Count);
    }

    [Fact]
    public void DashboardIdChange_ReloadsSameComponentInstance()
    {
        _handler.SetJsonResponse("api/dashboards/1", MakeDashboard(1) with { Name = "First dashboard" });
        _handler.SetJsonResponse("api/dashboards/2", MakeDashboard(2) with { Name = "Second dashboard" });
        var cut = Render<DashboardEdit>(p => p.Add(x => x.Id, 1));

        cut.Render(p => p.Add(x => x.Id, 2));

        cut.WaitForAssertion(() => Assert.Contains("Second dashboard", cut.Markup));
        Assert.DoesNotContain("First dashboard", cut.Markup);
    }

    [Fact]
    public void NonAdmin_RedirectsToHome()
    {
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: false);
        var cut = Render<DashboardEdit>(p => p.Add(x => x.Id, 1));
        // A non-admin is redirected to the app root and the dashboard is never fetched.
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("/", nav.Uri);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("api/dashboards/1"));
    }

    [Fact]
    public void AddWidget_IncreasesCount()
    {
        var cut = RenderDashboard();
        var widgets = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var initialCount = widgets.Count;

        var method = typeof(DashboardEdit).GetMethod("AddWidget", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, []);

        widgets = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal(initialCount + 1, widgets.Count);
    }

    [Fact]
    public void RemoveWidget_DecreasesCount()
    {
        var cut = RenderDashboard();
        var widgetsList = typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var firstWidget = ((System.Collections.IList)widgetsList)[0];

        var method = typeof(DashboardEdit).GetMethod("RemoveWidget", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [firstWidget]);

        var count = ((System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!).Count;
        Assert.Equal(1, count);
    }

    [Fact]
    public void OnDragStart_SetsDragIndex()
    {
        var cut = RenderDashboard();
        var method = typeof(DashboardEdit).GetMethod("OnDragStart", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [0]);

        var dragIndex = typeof(DashboardEdit).GetField("_dragIndex", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Equal(0, (int?)dragIndex);
    }

    [Fact]
    public void OnDragEnd_ClearsDragIndex()
    {
        var cut = RenderDashboard();
        typeof(DashboardEdit).GetField("_dragIndex", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, (int?)1);

        var method = typeof(DashboardEdit).GetMethod("OnDragEnd", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, []);

        var dragIndex = typeof(DashboardEdit).GetField("_dragIndex", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Null(dragIndex);
    }

    [Fact]
    public void OnDrop_ReordersWidgets()
    {
        var cut = RenderDashboard();
        typeof(DashboardEdit).GetField("_dragIndex", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, (int?)0);

        var method = typeof(DashboardEdit).GetMethod("OnDrop", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [1]);

        // FRONTTEST-006: the dragged widget (Servers) now follows Pipelines, and the rows follow the
        // new order; resetting the drag alone would leave both untouched.
        Assert.Equal([("Pipelines", 0), ("Servers", 1)], TitlesAndRows(cut));
        var dragIndex = typeof(DashboardEdit).GetField("_dragIndex", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Null(dragIndex);
    }

    private static List<(string Title, int Row)> TitlesAndRows(IRenderedComponent<DashboardEdit> cut)
    {
        var widgets = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        return widgets.Cast<object>()
            .Select(widget => ((string)widget.GetType().GetProperty("Title")!.GetValue(widget)!,
                (int)widget.GetType().GetProperty("Row")!.GetValue(widget)!))
            .ToList();
    }

    [Fact]
    public void OnDrop_NullDragIndex_LeavesTheOrderUnchanged()
    {
        var cut = RenderDashboard();
        typeof(DashboardEdit).GetField("_dragIndex", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, null);

        var widgetsBefore = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var first = widgetsBefore[0];
        var second = widgetsBefore[1];

        var method = typeof(DashboardEdit).GetMethod("OnDrop", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [0]);

        // A null drag index makes OnDrop short-circuit - the widget order is left untouched.
        var widgetsAfter = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Same(first, widgetsAfter[0]);
        Assert.Same(second, widgetsAfter[1]);
    }

    [Fact]
    public void MoveUp_FirstWidget_LeavesTheOrderUnchanged()
    {
        var cut = RenderDashboard();
        var widgets = typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var firstWidget = ((System.Collections.IList)widgets)[0];

        var method = typeof(DashboardEdit).GetMethod("MoveUp", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [firstWidget]);

        // First widget should still be at index 0
        var afterWidgets = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Same(firstWidget, afterWidgets[0]);
    }

    [Fact]
    public void MoveDown_LastWidget_LeavesTheOrderUnchanged()
    {
        var cut = RenderDashboard();
        var widgets = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var lastWidget = widgets[widgets.Count - 1];

        var method = typeof(DashboardEdit).GetMethod("MoveDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [lastWidget]);

        var afterWidgets = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Same(lastWidget, afterWidgets[afterWidgets.Count - 1]);
    }

    [Fact]
    public void MoveUp_SecondWidget_SwapsWithFirst()
    {
        var cut = RenderDashboard();
        var widgets = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var secondWidget = widgets[1];

        var method = typeof(DashboardEdit).GetMethod("MoveUp", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [secondWidget]);

        var afterWidgets = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Same(secondWidget, afterWidgets[0]);
    }

    [Fact]
    public void MoveDown_FirstWidget_SwapsWithSecond()
    {
        var cut = RenderDashboard();
        var widgets = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var firstWidget = widgets[0];

        var method = typeof(DashboardEdit).GetMethod("MoveDown", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [firstWidget]);

        var afterWidgets = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Same(firstWidget, afterWidgets[1]);
    }

    [Fact]
    public void ToggleConfig_TogglesShowConfig()
    {
        var cut = RenderDashboard();
        var widgets = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var widget = widgets[0]!;

        var method = typeof(DashboardEdit).GetMethod("ToggleConfig", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [widget]);

        var showConfig = (bool)widget.GetType().GetProperty("ShowConfig")!.GetValue(widget)!;
        Assert.True(showConfig);

        method.Invoke(cut.Instance, [widget]);
        showConfig = (bool)widget.GetType().GetProperty("ShowConfig")!.GetValue(widget)!;
        Assert.False(showConfig);
    }

    [Theory]
    [InlineData(DashboardWidgetType.ServerCount, "dns")]
    [InlineData(DashboardWidgetType.PipelineActivity, "account_tree")]
    [InlineData(DashboardWidgetType.RecentRuns, "history")]
    [InlineData(DashboardWidgetType.ServerList, "list")]
    [InlineData(DashboardWidgetType.TaskSummary, "task_alt")]
    [InlineData(DashboardWidgetType.BuildSuccess, "check_circle")]
    [InlineData(DashboardWidgetType.Custom, "widgets")]
    public void GetWidgetIcon_ReturnsExpectedIcon(DashboardWidgetType type, string expectedIcon)
    {
        var method = typeof(DashboardEdit).GetMethod("GetWidgetIcon", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (string)method.Invoke(null, [type])!;
        Assert.Equal(expectedIcon, result);
    }

    [Fact]
    public async Task SaveDashboard_EmptyName_ShowsWarning()
    {
        var cut = RenderDashboard();
        typeof(DashboardEdit).GetField("_name", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "");

        var requestsBefore = _handler.Requests.Count;
        var method = typeof(DashboardEdit).GetMethod("SaveDashboard", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        // Empty name short-circuits before _saving is ever set: no update request is sent.
        var saving = (bool)typeof(DashboardEdit).GetField("_saving", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.False(saving);
        Assert.DoesNotContain(_handler.Requests.Skip(requestsBefore), r => r.Method == "PUT" || r.Method == "POST");
    }

    [Fact]
    public async Task SaveDashboard_WithValidName_Saves()
    {
        var dashboard = MakeDashboard();
        _handler.SetJsonResponse("api/dashboards/1", dashboard);
        var cut = RenderDashboard();

        typeof(DashboardEdit).GetField("_name", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "Updated Dashboard");

        var method = typeof(DashboardEdit).GetMethod("SaveDashboard", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        // A valid name drives a real PUT to the dashboard's id endpoint (the load only GETs it).
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/dashboards/1"));
    }

    [Fact]
    public void RecalculateRows_SetsSequentialRows()
    {
        // FRONTTEST-006: the fixture already holds rows 0 and 1, so start from rows that are NOT
        // sequential (7 and 3); only a real reindexing turns them into 0 and 1.
        _handler.SetJsonResponse("api/dashboards/1", MakeDashboard(1) with
        {
            Widgets = [.. MakeDashboard(1).Widgets.Select(widget => widget with { Row = widget.Id == 1 ? 7 : 3 })]
        });
        var cut = Render<DashboardEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Equal(2, TitlesAndRows(cut).Count), TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(TitlesAndRows(cut), widget => widget.Row is 0 or 1);
        var method = typeof(DashboardEdit).GetMethod("RecalculateRows", BindingFlags.NonPublic | BindingFlags.Instance)!;

        method.Invoke(cut.Instance, []);

        Assert.Equal([0, 1], TitlesAndRows(cut).Select(widget => widget.Row));
    }

    [Fact]
    public void NonExistentDashboard_RedirectsToList()
    {
        _handler.SetResponse("api/dashboards/999", System.Net.HttpStatusCode.NotFound);
        Render<DashboardEdit>(p => p.Add(x => x.Id, 999));
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.Contains("dashboards", nav.Uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Renders_WithMultipleWidgets()
    {
        var dashboard = MakeDashboard() with
        {
            Widgets =
            [
                new DashboardWidgetDto { Id = 1, WidgetType = DashboardWidgetType.ServerCount, Title = "Servers", Row = 0, Column = 0, Width = 1, Height = 1, IsVisible = true },
                new DashboardWidgetDto { Id = 2, WidgetType = DashboardWidgetType.PipelineActivity, Title = "Pipelines", Row = 1, Column = 0, Width = 2, Height = 1, IsVisible = true },
                new DashboardWidgetDto { Id = 3, WidgetType = DashboardWidgetType.RecentRuns, Title = "Runs", Row = 2, Column = 0, Width = 1, Height = 2, IsVisible = true },
                new DashboardWidgetDto { Id = 4, WidgetType = DashboardWidgetType.TaskSummary, Title = "Tasks", Row = 3, Column = 0, Width = 1, Height = 1, IsVisible = false },
                new DashboardWidgetDto { Id = 5, WidgetType = DashboardWidgetType.Custom, Title = "Custom", Row = 4, Column = 0, Width = 1, Height = 1, IsVisible = true, ConfigurationJson = "{\"key\":\"value\"}" }
            ]
        };
        _handler.SetJsonResponse("api/dashboards/2", dashboard);
        var cut = Render<DashboardEdit>(p => p.Add(x => x.Id, 2));
        cut.WaitForState(() => !cut.Markup.Contains("rz-spinner"), TimeSpan.FromSeconds(2));

        // All five widgets from the stub are mapped into the editor list.
        var widgets = (System.Collections.IList)typeof(DashboardEdit).GetField("_widgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal(5, widgets.Count);
    }
}
