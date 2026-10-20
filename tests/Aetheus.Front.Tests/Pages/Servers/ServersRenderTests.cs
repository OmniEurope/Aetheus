// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers;
using Aetheus.Front.Resources;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using ServersPage = Aetheus.Front.Components.Servers.Servers;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServersRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    private static readonly BindingFlags InstPriv = BindingFlags.NonPublic | BindingFlags.Instance;
    public ServersRenderTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PaginatedResult<ServerDto> EmptyPage() => new()
    {
        Items = [],
        TotalCount = 0
    };

    private static PaginatedResult<ServerDto> TwoServers() => new()
    {
        Items =
        [
            new ServerDto
            {
                Id = 1, Name = "web-01", Hostname = "10.0.0.1",
                Type = ServerType.Docker, Status = ServerStatus.Online,
                OsDescription = "Ubuntu 22.04", AgentVersion = "1.2.3", AgentProtocolVersion = 2,
                Tags = ["prod"], LastHeartbeat = DateTime.UtcNow.AddSeconds(-10)
            },
            new ServerDto
            {
                Id = 2, Name = "db-01", Hostname = "10.0.0.2",
                Type = ServerType.Normal, Status = ServerStatus.Offline,
                OsDescription = "Debian 11", AgentVersion = "1.2.2", AgentProtocolVersion = 2,
                Tags = ["prod", "db"], LastHeartbeat = DateTime.UtcNow.AddHours(-3)
            }
        ],
        TotalCount = 2
    };

    // ── Render ────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_EmptyList_ProducesMarkup()
    {
        _handler.SetJsonResponse("api/servers", EmptyPage());
        var cut = Render<ServersPage>();

        // The grid's LoadData fires on render and GETs the paginated server list.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/servers"));
    }

    [Fact]
    public void Renders_WithTwoServers_ShowsServerNames()
    {
        _handler.SetJsonResponse("api/servers", TwoServers());
        var cut = Render<ServersPage>();

        // Both server names from the stubbed page render into the grid.
        cut.WaitForState(() => cut.Markup.Contains("web-01"));
        Assert.Contains("db-01", cut.Markup);
        Assert.DoesNotContain("· P2", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CompatibilityBadge_TooltipUsesStatusWithoutProtocolReason()
    {
        var localizer = Services.GetRequiredService<IStringLocalizer<AppStrings>>();
        localizer["AgentCompatibility_UpdateRequired"]
            .Returns(new LocalizedString("AgentCompatibility_UpdateRequired", "Update required"));
        localizer["AgentCompatibilityReason_UnsupportedProtocol"]
            .Returns(new LocalizedString(
                "AgentCompatibilityReason_UnsupportedProtocol",
                "Unsupported protocol P1"));

        var cut = Render<AgentCompatibilityBadge>(parameters => parameters.Add(component => component.Value,
            new AgentCompatibilityDto
            {
                Status = AgentCompatibilityStatus.UpdateRequired,
                Reason = AgentCompatibilityReason.UnsupportedProtocol
            }));

        Assert.Equal("Update required", cut.Find("span").GetAttribute("title"));
        Assert.DoesNotContain("Unsupported protocol P1", cut.Markup);
    }

    // ── _canWrite reflects permissions ────────────────────────────────────────

    [Fact]
    public void CanWrite_IsTrue_WhenAuthenticated()
    {
        _handler.SetJsonResponse("api/servers", EmptyPage());
        var cut = Render<ServersPage>();

        Assert.True(cut.Instance._canWrite);
    }

    // ── IsColumnVisible ───────────────────────────────────────────────────────

    [Fact]
    public void IsColumnVisible_AllDefaults_AreVisible()
    {
        _handler.SetJsonResponse("api/servers", EmptyPage());
        var cut = Render<ServersPage>();

        foreach (var col in new[] { "Type", "Hostname", "OsDescription", "AgentVersion", "Tags", "LastHeartbeat" })
            Assert.True(cut.Instance.IsColumnVisible(col), $"Column '{col}' should default to visible");
    }

    [Fact]
    public void IsColumnVisible_UnknownColumn_ReturnsTrue()
    {
        _handler.SetJsonResponse("api/servers", EmptyPage());
        var cut = Render<ServersPage>();

        Assert.True(cut.Instance.IsColumnVisible("NonExistentColumn"));
    }

    [Fact]
    public async Task IsColumnVisible_AfterHidingColumn_ReturnsFalse()
    {
        _handler.SetJsonResponse("api/servers", EmptyPage());
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("OnColumnVisibilityChanged", InstPriv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, ["Tags", false])!);

        Assert.False(cut.Instance.IsColumnVisible("Tags"));
    }

    [Fact]
    public async Task IsColumnVisible_AfterShowingHiddenColumn_ReturnsTrue()
    {
        _handler.SetJsonResponse("api/servers", EmptyPage());
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("OnColumnVisibilityChanged", InstPriv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, ["Hostname", false])!);
        Assert.False(cut.Instance.IsColumnVisible("Hostname"));

        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, ["Hostname", true])!);
        Assert.True(cut.Instance.IsColumnVisible("Hostname"));
    }

    // ── LoadColumnVisibilityAsync ─────────────────────────────────────────────

    [Fact]
    public async Task LoadColumnVisibilityAsync_NoStoredValue_LeavesDefaults()
    {
        _handler.SetJsonResponse("api/servers", EmptyPage());
        // JSInterop is Loose: getLocal returns null by default
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("LoadColumnVisibilityAsync", InstPriv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [])!);

        // All columns stay visible when nothing is stored
        Assert.True(cut.Instance.IsColumnVisible("Type"));
        Assert.True(cut.Instance.IsColumnVisible("Tags"));
    }

    // ── FilterByTag ───────────────────────────────────────────────────────────

    /// <summary>A tag clicked in a row becomes the Tags column's own filter, sent to the
    /// API.</summary>
    [Fact]
    public async Task FilterByTag_SetsTheTagsColumnFilter()
    {
        _handler.SetJsonResponse("api/servers", TwoServers());
        var cut = Render<ServersPage>();

        await cut.InvokeAsync(() => cut.Instance.FilterByTag("prod"));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            Uri.UnescapeDataString(request.Url).Contains("Filters[0].Field=Tags", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Value=prod", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FilterByTag_ReplacesThePreviousTag()
    {
        _handler.SetJsonResponse("api/servers", TwoServers());
        var cut = Render<ServersPage>();

        await cut.InvokeAsync(() => cut.Instance.FilterByTag("first"));
        await cut.InvokeAsync(() => cut.Instance.FilterByTag("second"));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            Uri.UnescapeDataString(request.Url).Contains("Filters[0].Value=second", StringComparison.Ordinal)));
    }

    // ── OnLoadData ────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnLoadData_PopulatesServersAndTotalCount()
    {
        _handler.SetJsonResponse("api/servers", TwoServers());
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("OnLoadData", InstPriv)!;
        var args = new GridLoadArgs { Skip = 0, Top = 25 };
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [args])!);

        var servers = (List<ServerDto>)typeof(ServersPage)
            .GetField("_servers", InstPriv)!
            .GetValue(cut.Instance)!;

        Assert.Equal(2, servers.Count);

        var total = (int)typeof(ServersPage)
            .GetField("_totalCount", InstPriv)!
            .GetValue(cut.Instance)!;
        Assert.Equal(2, total);
    }

    [Fact]
    public async Task OnLoadData_WithSort_CompletesWithoutThrowing()
    {
        _handler.SetJsonResponse("api/servers", TwoServers());
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("OnLoadData", InstPriv)!;
        var args = new GridLoadArgs
        {
            Skip = 0,
            Top = 25,
            Sorts = [new GridSortDescriptor("Name", GridSortOrder.Ascending)]
        };

        var ex = await Record.ExceptionAsync(async () =>
            await cut.InvokeAsync(async () =>
                await (Task)method.Invoke(cut.Instance, [args])!));

        Assert.Null(ex);
    }

    [Fact]
    public async Task OnLoadData_WithDescendingSort_CompletesWithoutThrowing()
    {
        _handler.SetJsonResponse("api/servers", TwoServers());
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("OnLoadData", InstPriv)!;
        var args = new GridLoadArgs
        {
            Skip = 0,
            Top = 10,
            Sorts = [new GridSortDescriptor("Status", GridSortOrder.Descending)]
        };

        var ex = await Record.ExceptionAsync(async () =>
            await cut.InvokeAsync(async () =>
                await (Task)method.Invoke(cut.Instance, [args])!));

        Assert.Null(ex);
    }

    // ── OfflineReason helper ──────────────────────────────────────────────────

    [Fact]
    public void OfflineReason_RecentHeartbeat_ReturnsEmpty()
    {
        _handler.SetJsonResponse("api/servers", EmptyPage());
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("OfflineReason", InstPriv)!;
        var server = new ServerDto { LastHeartbeat = DateTime.UtcNow.AddSeconds(-10) };
        var result = (string)method.Invoke(cut.Instance, [server])!;

        // Recent heartbeat = no offline reason
        Assert.NotNull(result);
    }

    // ── ToggleColumnChooser ───────────────────────────────────────────────────

    [Fact]
    public void ToggleColumnChooser_TogglesShowFlag()
    {
        _handler.SetJsonResponse("api/servers", EmptyPage());
        var cut = Render<ServersPage>();

        var toggle = typeof(ServersPage).GetMethod("ToggleColumnChooser", InstPriv)!;

        toggle.Invoke(cut.Instance, null);
        var shown = (bool)typeof(ServersPage).GetField("_showColumnChooser", InstPriv)!.GetValue(cut.Instance)!;
        Assert.True(shown);

        toggle.Invoke(cut.Instance, null);
        shown = (bool)typeof(ServersPage).GetField("_showColumnChooser", InstPriv)!.GetValue(cut.Instance)!;
        Assert.False(shown);
    }
}
