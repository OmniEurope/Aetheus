// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ServersPage = Aetheus.Front.Components.Servers.Servers;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Deep coverage for Servers.razor.cs - OfflineReason, OnLoadData with sort/filter,
/// ClearFilters, FilterByTag, PersistColumnVisibilityAsync, LoadColumnVisibilityAsync,
/// column chooser toggle, DisposeAsync, OnPermissionsChanged.
/// Dialog.Confirm (OnUpdateAllAgents, OnRetireServer) excluded.
/// </summary>
public class ServersDeepCoverageTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServersDeepCoverageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ServerDto MakeServer(int id, ServerStatus status = ServerStatus.Online,
        ServerType type = ServerType.Normal) => new()
        {
            Id = id,
            Name = $"srv-{id}",
            Hostname = $"10.0.0.{id}",
            Type = type,
            Status = status,
            AgentVersion = "3.0.0",
            Tags = ["prod"],
            LastHeartbeat = status == ServerStatus.Online ? DateTime.Now : DateTime.Now.AddMinutes(-30)
        };

    private void SetupServers(int count = 3)
    {
        var servers = Enumerable.Range(1, count).Select(i => MakeServer(i)).ToList();
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = servers,
            TotalCount = count,
            Page = 1,
            PageSize = 20
        });
    }

    // ── Test 1: OnLoadData with sort ascending ────────────────────────────────

    [Fact]
    public async Task OnLoadData_WithSort_LoadsServers()
    {
        SetupServers(5);
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("OnLoadData", Priv)!;
        var args = new GridLoadArgs
        {
            Skip = 0,
            Top = 20,
            Sorts = [new GridSortDescriptor("Name", GridSortOrder.Ascending)]
        };

        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [args])!);

        var servers = (List<ServerDto>)typeof(ServersPage).GetField("_servers", Priv)!.GetValue(cut.Instance)!;
        Assert.NotEmpty(servers);
    }

    // ── Test 2: OnLoadData with sort descending ───────────────────────────────

    [Fact]
    public async Task OnLoadData_WithDescendingSort_LoadsServers()
    {
        SetupServers(3);
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("OnLoadData", Priv)!;
        var args = new GridLoadArgs
        {
            Skip = 0,
            Top = 10,
            Sorts = [new GridSortDescriptor("Status", GridSortOrder.Descending)]
        };

        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [args])!);

        var loading = (bool)typeof(ServersPage).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
    }

    // ── Test 3: OnLoadData with no sorts ─────────────────────────────────────

    [Fact]
    public async Task OnLoadData_NoSorts_LoadsServers()
    {
        SetupServers(2);
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("OnLoadData", Priv)!;
        var args = new GridLoadArgs { Skip = 0, Top = 10 };

        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [args])!);

        var count = (int)typeof(ServersPage).GetField("_totalCount", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, count);
    }

    // ── Test 5: FilterByTag sets _search ─────────────────────────────────────

    /// <summary>A tag clicked in a row becomes the Tags column's own filter, sent to the
    /// API.</summary>
    [Fact]
    public async Task FilterByTag_SetsTheTagsColumnFilter()
    {
        SetupServers(2);
        var cut = Render<ServersPage>();

        await cut.InvokeAsync(() => cut.Instance.FilterByTag("prod"));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            Uri.UnescapeDataString(request.Url).Contains("Filters[0].Field=Tags", StringComparison.Ordinal)
            && Uri.UnescapeDataString(request.Url).Contains("Filters[0].Value=prod", StringComparison.Ordinal)));
    }

    // ── Test 6: OfflineReason returns non-empty for offline server ────────────

    [Fact]
    public void OfflineReason_NeverReported_ReturnsNeverReportedReason()
    {
        SetupServers(1);
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("OfflineReason", Priv)!;
        // Heartbeat at default (year 0001 < 2000) → the "never reported" branch.
        var server = MakeServer(99, ServerStatus.Offline);
        server = server with { LastHeartbeat = default };
        var reason = (string)method.Invoke(cut.Instance, [server])!;
        Assert.Equal("ServerOfflineNeverReported", reason);
    }

    // ── Test 7: OfflineReason for a reported server uses the elapsed-time branch ──

    [Fact]
    public void OfflineReason_RecentlyReported_ReturnsElapsedReason()
    {
        SetupServers(1);
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("OfflineReason", Priv)!;
        // A real (year >= 2000) heartbeat → the formatted "no heartbeat for X" branch,
        // which is distinct from the never-reported key.
        var server = MakeServer(98, ServerStatus.Online);
        var reason = (string)method.Invoke(cut.Instance, [server])!;
        Assert.Contains("ServerOfflineNoHeartbeat", reason);
        Assert.NotEqual("ServerOfflineNeverReported", reason);
    }

    // ── Test 8: ToggleColumnChooser flips _showColumnChooser ─────────────────

    [Fact]
    public async Task ToggleColumnChooser_FlipsFlag()
    {
        SetupServers(1);
        var cut = Render<ServersPage>();

        var field = typeof(ServersPage).GetField("_showColumnChooser", Priv)!;
        Assert.False((bool)field.GetValue(cut.Instance)!); // closed by default

        var method = typeof(ServersPage).GetMethod("ToggleColumnChooser", Priv)!;
        await cut.InvokeAsync(() => method.Invoke(cut.Instance, []));
        Assert.True((bool)field.GetValue(cut.Instance)!); // opened

        await cut.InvokeAsync(() => method.Invoke(cut.Instance, []));
        Assert.False((bool)field.GetValue(cut.Instance)!); // toggled back closed
    }

    // ── Test 9: IsColumnVisible - known key ──────────────────────────────────

    [Fact]
    public void IsColumnVisible_TypeKey_ReturnsTrue()
    {
        SetupServers(1);
        var cut = Render<ServersPage>();

        Assert.True(cut.Instance.IsColumnVisible("Type"));
    }

    // ── Test 10: IsColumnVisible - unknown key returns true ──────────────────

    [Fact]
    public void IsColumnVisible_UnknownKey_ReturnsTrue()
    {
        SetupServers(1);
        var cut = Render<ServersPage>();

        Assert.True(cut.Instance.IsColumnVisible("NonExistentColumn"));
    }

    // ── Test 11: PersistColumnVisibilityAsync stores hidden columns ──────────

    [Fact]
    public async Task PersistColumnVisibilityAsync_StoresHiddenColumns()
    {
        SetupServers(1);
        var cut = Render<ServersPage>();

        // Hide one column
        var visibility = (Dictionary<string, bool>)typeof(ServersPage)
            .GetField("_columnVisibility", Priv)!.GetValue(cut.Instance)!;
        visibility["Tags"] = false;

        var method = typeof(ServersPage).GetMethod("PersistColumnVisibilityAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // R1-66: the hidden-column set is the user's preference, kept on the server.
        Assert.Equal("Tags", Services.GetRequiredService<Aetheus.Front.Tests.TestDoubles.InMemoryUserPreferences>()
            .Read<string>(PreferenceKeys.ServerHiddenColumns));
    }

    // ── Test 12: OnPermissionsChanged refreshes _canWrite ────────────────────

    [Fact]
    public void OnPermissionsChanged_RefreshesCanWrite()
    {
        SetupServers(1);
        var cut = Render<ServersPage>();

        // The test harness grants Server write permission, so RefreshCanWrite computes true.
        var field = typeof(ServersPage).GetField("_canWrite", Priv)!;
        // Force the stale state the event is meant to correct.
        field.SetValue(cut.Instance, false);

        var method = typeof(ServersPage).GetMethod("OnPermissionsChanged", Priv)!;
        method.Invoke(cut.Instance, []);

        // The handler must recompute _canWrite from PermissionService (back to true).
        Assert.True((bool)field.GetValue(cut.Instance)!);
    }

    // ── Test 13: DisposeAsync unsubscribes and disposes hub ──────────────────

    [Fact]
    public async Task DisposeAsync_UnsubscribesAndDisposesHub()
    {
        SetupServers(1);
        var cut = Render<ServersPage>();

        await cut.Instance.DisposeAsync();

        // Dispose tears down the hub connection (set back to null).
        var hub = typeof(ServersPage).GetField("_hubConnection", Priv)!.GetValue(cut.Instance);
        Assert.Null(hub);
    }

    // ── Test 14: OnColumnVisibilityChanged updates dictionary ────────────────

    [Fact]
    public async Task OnColumnVisibilityChanged_UpdatesVisibility()
    {
        SetupServers(1);
        var cut = Render<ServersPage>();

        var method = typeof(ServersPage).GetMethod("OnColumnVisibilityChanged", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["Tags", false])!);

        Assert.False(cut.Instance.IsColumnVisible("Tags"));
    }
}
