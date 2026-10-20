// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// The dashboard editor driven through its markup: the widget rows, their buttons, the drag events
/// and the Save request the faked API receives. No private member of the page is read (RFL-1).
/// </summary>
public class DashboardEditTests : BunitContext
{
    private const string WidgetRow = "[role=listitem]";

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
        return RenderLoaded(id);
    }

    private IRenderedComponent<DashboardEdit> RenderLoaded(int id)
    {
        var cut = Render<DashboardEdit>(p => p.Add(x => x.Id, id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(WidgetRow)));
        return cut;
    }

    private static List<string> Titles(IRenderedComponent<DashboardEdit> cut) =>
        cut.FindAll($"{WidgetRow} input[aria-label=WidgetTitle]").Select(input => input.GetAttribute("value") ?? "").ToList();

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<DashboardEdit> cut, int index) => cut.FindAll(WidgetRow)[index];

    /// <summary>A widget row's action: Settings is the visible button, the others are entries of the row's
    /// "More actions" menu (STD-BTN detection 11), opened here the way a user does.</summary>
    private static AngleSharp.Dom.IElement RowButton(IRenderedComponent<DashboardEdit> cut, int index, string label)
    {
        AngleSharp.Dom.IElement? MenuEntry() => Row(cut, index).QuerySelectorAll(".omni-menu__item")
            .SingleOrDefault(item => item.TextContent.Trim() == label);

        var visible = Row(cut, index).QuerySelectorAll("button:not(.omni-menu__item)")
            .SingleOrDefault(button => button.TextContent.Trim() == label);
        if (visible is not null) return visible;
        if (MenuEntry() is { } open) return open;
        Row(cut, index).QuerySelector(".omni-overflow-menu__trigger")!.Click();
        return MenuEntry() ?? throw new InvalidOperationException($"No row action {label}.");
    }

    private static Task ClickAsync(AngleSharp.Dom.IElement element) => element.ClickAsync(new MouseEventArgs());

    private static AngleSharp.Dom.IElement SaveButton(IRenderedComponent<DashboardEdit> cut) =>
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "SaveDashboard");

    /// <summary>Saves and returns what the page sent: the dashboard name and the widgets as (title, row).</summary>
    private async Task<(string? Name, List<(string Title, int Row)> Widgets)> SaveAndReadRequestAsync(IRenderedComponent<DashboardEdit> cut)
    {
        var before = _handler.RequestDetails.Count;
        await ClickAsync(SaveButton(cut));
        var put = Assert.Single(_handler.RequestDetails.Skip(before), request => request.Method == "PUT");
        using var json = JsonDocument.Parse(put.Body!);
        string? name = null;
        List<(string, int)> widgets = [];
        foreach (var property in json.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
                name = property.Value.GetString();
            if (!property.Name.Equals("widgets", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var widget in property.Value.EnumerateArray())
            {
                string title = "";
                var row = -1;
                foreach (var field in widget.EnumerateObject())
                {
                    if (field.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) title = field.Value.GetString()!;
                    if (field.Name.Equals("row", StringComparison.OrdinalIgnoreCase)) row = field.Value.GetInt32();
                }
                widgets.Add((title, row));
            }
        }
        return (name, widgets);
    }

    [Fact]
    public void Renders_DashboardEditPage_WithData()
    {
        var cut = RenderDashboard();
        // OnInitializedAsync maps the loaded dashboard into the name field and the widget editor list.
        Assert.Equal("Main Dashboard", cut.Find("input#oe-formfield-pages-dashboard-dashboardedit-1").GetAttribute("value"));
        Assert.Equal(["Servers", "Pipelines"], Titles(cut));
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
    public async Task AddWidget_IncreasesCount()
    {
        var cut = RenderDashboard();
        var initialCount = cut.FindAll(WidgetRow).Count;

        await ClickAsync(cut.FindMenuItem("AddWidget")!);

        Assert.Equal(initialCount + 1, cut.FindAll(WidgetRow).Count);
        Assert.Equal("WidgetDefaultName 3", Titles(cut).Last());
    }

    [Fact]
    public async Task RemoveWidget_DecreasesCount()
    {
        var cut = RenderDashboard();

        await ClickAsync(RowButton(cut, 0, "Delete"));

        Assert.Single(cut.FindAll(WidgetRow));
        Assert.Equal(["Pipelines"], Titles(cut));
    }

    [Fact]
    public async Task OnDragStart_SetsDragIndex()
    {
        var cut = RenderDashboard();

        await Row(cut, 0).TriggerEventAsync("ondragstart", new DragEventArgs());

        // The dragged row is the one framed with the accent border.
        Assert.Contains("omni-u-border-accent", Row(cut, 0).InnerHtml);
        Assert.DoesNotContain("omni-u-border-accent", Row(cut, 1).InnerHtml);
    }

    [Fact]
    public async Task OnDragEnd_ClearsDragIndex()
    {
        var cut = RenderDashboard();
        await Row(cut, 1).TriggerEventAsync("ondragstart", new DragEventArgs());
        Assert.Contains("omni-u-border-accent", Row(cut, 1).InnerHtml);

        await Row(cut, 1).TriggerEventAsync("ondragend", new DragEventArgs());

        Assert.DoesNotContain("omni-u-border-accent", cut.Markup);
    }

    [Fact]
    public async Task OnDrop_ReordersWidgets()
    {
        var cut = RenderDashboard();
        await Row(cut, 0).TriggerEventAsync("ondragstart", new DragEventArgs());

        await Row(cut, 1).TriggerEventAsync("ondrop", new DragEventArgs());

        // FRONTTEST-006: the dragged widget (Servers) now follows Pipelines, and the rows follow the
        // new order (read from the saved request); resetting the drag alone would leave both untouched.
        Assert.Equal(["Pipelines", "Servers"], Titles(cut));
        Assert.DoesNotContain("omni-u-border-accent", cut.Markup);
        var saved = await SaveAndReadRequestAsync(cut);
        Assert.Equal([("Pipelines", 0), ("Servers", 1)], saved.Widgets);
    }

    [Fact]
    public async Task OnDrop_NullDragIndex_LeavesTheOrderUnchanged()
    {
        var cut = RenderDashboard();

        await Row(cut, 0).TriggerEventAsync("ondrop", new DragEventArgs());

        // Nothing is being dragged, so a drop short-circuits - the widget order is left untouched.
        Assert.Equal(["Servers", "Pipelines"], Titles(cut));
    }

    [Fact]
    public async Task MoveUp_FirstWidget_LeavesTheOrderUnchanged()
    {
        var cut = RenderDashboard();

        // The first widget cannot go up: its button is disabled and a click changes nothing.
        Assert.True(RowButton(cut, 0, "MoveUp").HasAttribute("disabled"));
        await ClickAsync(RowButton(cut, 0, "MoveUp"));

        Assert.Equal(["Servers", "Pipelines"], Titles(cut));
    }

    [Fact]
    public async Task MoveDown_LastWidget_LeavesTheOrderUnchanged()
    {
        var cut = RenderDashboard();

        Assert.True(RowButton(cut, 1, "MoveDown").HasAttribute("disabled"));
        await ClickAsync(RowButton(cut, 1, "MoveDown"));

        Assert.Equal(["Servers", "Pipelines"], Titles(cut));
    }

    [Fact]
    public async Task MoveUp_SecondWidget_SwapsWithFirst()
    {
        var cut = RenderDashboard();

        await ClickAsync(RowButton(cut, 1, "MoveUp"));

        Assert.Equal(["Pipelines", "Servers"], Titles(cut));
    }

    [Fact]
    public async Task MoveDown_FirstWidget_SwapsWithSecond()
    {
        var cut = RenderDashboard();

        await ClickAsync(RowButton(cut, 0, "MoveDown"));

        Assert.Equal(["Pipelines", "Servers"], Titles(cut));
    }

    [Fact]
    public async Task ToggleConfig_TogglesShowConfig()
    {
        var cut = RenderDashboard();
        Assert.Empty(Row(cut, 0).QuerySelectorAll("textarea"));

        await ClickAsync(RowButton(cut, 0, "Settings"));

        Assert.Single(Row(cut, 0).QuerySelectorAll("textarea"));
        Assert.Empty(Row(cut, 1).QuerySelectorAll("textarea"));

        await ClickAsync(RowButton(cut, 0, "Settings"));

        Assert.Empty(Row(cut, 0).QuerySelectorAll("textarea"));
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
        _handler.SetJsonResponse("api/dashboards/1", MakeDashboard(1) with
        {
            Widgets = [new DashboardWidgetDto { Id = 1, WidgetType = type, Title = "Only", Row = 0, Column = 0, Width = 1, Height = 1, IsVisible = true }]
        });
        var cut = RenderLoaded(1);

        // The widget's card shows its type's icon (second icon of the card, after the drag handle).
        var card = cut.FindComponents<OmniCard>()[1];
        Assert.Equal(MaterialIconMap.Resolve(expectedIcon), card.FindComponents<OmniIcon>()[1].Instance.Name);
    }

    [Fact]
    public async Task SaveDashboard_EmptyName_ShowsWarning()
    {
        var cut = RenderDashboard();
        cut.Find("input#oe-formfield-pages-dashboard-dashboardedit-1").Input("");

        var requestsBefore = _handler.Requests.Count;
        await ClickAsync(SaveButton(cut));

        // Empty name short-circuits before the save starts: no update request is sent, the page is
        // not left busy, and the user is told a field is required.
        Assert.DoesNotContain(_handler.Requests.Skip(requestsBefore), r => r.Method == "PUT" || r.Method == "POST");
        Assert.DoesNotContain("btn-busy", SaveButton(cut).ClassName);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniEurope.Blazor.Components.OmniSeverity.Warning && toast.Detail == "RequiredFields");
    }

    [Fact]
    public async Task SaveDashboard_WithValidName_Saves()
    {
        var cut = RenderDashboard();
        cut.Find("input#oe-formfield-pages-dashboard-dashboardedit-1").Input("Updated Dashboard");

        var saved = await SaveAndReadRequestAsync(cut);

        // A valid name drives a real PUT to the dashboard's id endpoint (the load only GETs it).
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/dashboards/1"));
        Assert.Equal("Updated Dashboard", saved.Name);
    }

    [Fact]
    public async Task RecalculateRows_SetsSequentialRows()
    {
        // FRONTTEST-006: the fixture holds rows that are NOT sequential (7 and 3); only a real
        // reindexing, which every move triggers, turns them into 0 and 1. The widgets load in row order
        // (Pipelines, then Servers).
        _handler.SetJsonResponse("api/dashboards/1", MakeDashboard(1) with
        {
            Widgets = [.. MakeDashboard(1).Widgets.Select(widget => widget with { Row = widget.Id == 1 ? 7 : 3 })]
        });
        var cut = RenderLoaded(1);
        Assert.Equal(["Pipelines", "Servers"], Titles(cut));

        await ClickAsync(RowButton(cut, 0, "MoveDown"));

        var saved = await SaveAndReadRequestAsync(cut);
        Assert.Equal([("Servers", 0), ("Pipelines", 1)], saved.Widgets);
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
        var cut = RenderLoaded(2);

        // All five widgets from the stub are mapped into the editor list; the preview shows the four visible ones.
        Assert.Equal(["Servers", "Pipelines", "Runs", "Tasks", "Custom"], Titles(cut));
        Assert.Equal(4, cut.FindAll(".dashboard-widget-preview").Count);
    }
}
