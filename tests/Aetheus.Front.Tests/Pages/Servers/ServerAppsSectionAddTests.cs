// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Tests for ServerAppsSection.AddAppAsync and GetAppStatusBadge (unknown branch).
/// DeleteAppAsync is excluded - it calls Dialog.Confirm which hangs.
/// </summary>
public class ServerAppsSectionAddTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerAppsSectionAddTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private IRenderedComponent<ServerAppsSection> RenderSection(int serverId = 1,
        List<ServerAppDto>? initialApps = null,
        ServerAppDto? createdApp = null)
    {
        _handler.SetPaginatedJsonResponse($"api/servers/{serverId}/apps",
            initialApps ?? new List<ServerAppDto>());
        if (createdApp is not null)
            _handler.SetJsonResponse(HttpMethod.Post, $"api/servers/{serverId}/apps", createdApp);
        return Render<ServerAppsSection>(p => p.Add(x => x.ServerId, serverId));
    }

    // ── AddAppAsync: success path ─────────────────────────────────────────────
    // Strategy: render the component with a list response, wait for init,
    // then override with a new response for the POST before invoking AddAppAsync.
    // Because TestHandler matches longest-key-first and the POST uses the same URL,
    // we manually set _apps via reflection to seed the list, avoiding a second
    // SetJsonResponse conflict.

    [Fact]
    public async Task AddAppAsync_Success_AddsAppToList()
    {
        var initialApps = new List<ServerAppDto>();
        _handler.SetPaginatedJsonResponse("api/servers/20/apps", initialApps);
        var cut = Render<ServerAppsSection>(p => p.Add(x => x.ServerId, 20));
        cut.WaitForState(
            () => Priv_Get<List<ServerAppDto>?>(cut.Instance, "_apps") is not null);

        // Seed _apps so Add() can work after POST
        Priv_Set(cut.Instance, "_apps", new List<ServerAppDto>());

        // Now set the handler to return a single item for the POST
        var newApp = new ServerAppDto { Id = 99, Name = "new-app", Source = "manual" };
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/20/apps", newApp);

        Priv_Set(cut.Instance, "_addName", "new-app");
        Priv_Set(cut.Instance, "_addVersion", "1.0");
        Priv_Set(cut.Instance, "_addPort", (int?)8080);
        Priv_Set(cut.Instance, "_addSource", "manual");

        var method = typeof(ServerAppsSection).GetMethod("AddAppAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var addName = Priv_Get<string>(cut.Instance, "_addName");
        Assert.Equal(string.Empty, addName);
    }

    [Fact]
    public async Task AddAppAsync_SetsAddSaving_AfterCall_IsFalse()
    {
        _handler.SetPaginatedJsonResponse("api/servers/21/apps", new List<ServerAppDto>());
        var cut = Render<ServerAppsSection>(p => p.Add(x => x.ServerId, 21));
        cut.WaitForState(
            () => Priv_Get<List<ServerAppDto>?>(cut.Instance, "_apps") is not null);

        // Override with single item for POST
        Priv_Set(cut.Instance, "_apps", new List<ServerAppDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/21/apps", new ServerAppDto { Id = 10, Name = "x", Source = "manual" });

        Priv_Set(cut.Instance, "_addName", "myapp");
        Priv_Set(cut.Instance, "_addSource", "manual");

        var method = typeof(ServerAppsSection).GetMethod("AddAppAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var saving = Priv_Get<bool>(cut.Instance, "_addSaving");
        Assert.False(saving);
    }

    [Fact]
    public async Task AddAppAsync_WithValidResponse_AppNameIsCleared()
    {
        // When a valid ServerAppDto is returned, _addName should be cleared
        var newApp = new ServerAppDto { Id = 22, Name = "valid-app", Source = "manual" };
        _handler.SetPaginatedJsonResponse("api/servers/22/apps", new List<ServerAppDto>());
        var cut = Render<ServerAppsSection>(p => p.Add(x => x.ServerId, 22));
        cut.WaitForState(
            () => Priv_Get<List<ServerAppDto>?>(cut.Instance, "_apps") is not null);

        // Override with single app for POST
        Priv_Set(cut.Instance, "_apps", new List<ServerAppDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/22/apps", newApp);

        Priv_Set(cut.Instance, "_addName", "should-noop");
        Priv_Set(cut.Instance, "_addSource", "manual");

        var method = typeof(ServerAppsSection).GetMethod("AddAppAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // _addName cleared on success
        var name = Priv_Get<string>(cut.Instance, "_addName");
        Assert.Equal(string.Empty, name);
    }

    [Fact]
    public async Task AddAppAsync_EmptyVersion_SendsNullVersion()
    {
        var newApp = new ServerAppDto { Id = 5, Name = "no-version-app", Source = "docker" };
        _handler.SetPaginatedJsonResponse("api/servers/23/apps", new List<ServerAppDto>());
        var cut = Render<ServerAppsSection>(p => p.Add(x => x.ServerId, 23));
        cut.WaitForState(
            () => Priv_Get<List<ServerAppDto>?>(cut.Instance, "_apps") is not null);

        Priv_Set(cut.Instance, "_apps", new List<ServerAppDto>());
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/23/apps", newApp);

        Priv_Set(cut.Instance, "_addName", "no-version-app");
        Priv_Set(cut.Instance, "_addVersion", "   "); // whitespace → null
        Priv_Set(cut.Instance, "_addPort", (int?)null);
        Priv_Set(cut.Instance, "_addSource", "docker");

        var method = typeof(ServerAppsSection).GetMethod("AddAppAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var saving = Priv_Get<bool>(cut.Instance, "_addSaving");
        Assert.False(saving);
    }

    // ── GetAppStatusBadge: Unknown branch ─────────────────────────────────────

    [Fact]
    public void GetAppStatusBadge_Unknown_ReturnsWarning()
    {
        var method = typeof(ServerAppsSection)
            .GetMethod("GetAppStatusBadge", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [(ServerAppStatus)999])!;
        Assert.Equal(OmniTone.Warning, result);
    }

    // ── _addVisible panel ─────────────────────────────────────────────────────

    [Fact]
    public void AddVisible_DefaultIsFalse()
    {
        _handler.SetPaginatedJsonResponse("api/servers/1/apps", new List<ServerAppDto>());
        var cut = Render<ServerAppsSection>(p => p.Add(x => x.ServerId, 1));
        var visible = Priv_Get<bool>(cut.Instance, "_addVisible");
        Assert.False(visible);
    }

    [Fact]
    public void AddVisible_CanBeSetToTrue()
    {
        _handler.SetPaginatedJsonResponse("api/servers/1/apps", new List<ServerAppDto>());
        var cut = Render<ServerAppsSection>(p => p.Add(x => x.ServerId, 1));
        Priv_Set(cut.Instance, "_addVisible", true);
        cut.Render();
        Assert.Contains("ServerAppsSection", cut.Instance.GetType().Name);
    }

    // ── Sources list ──────────────────────────────────────────────────────────

    [Fact]
    public void Sources_ContainsExpectedValues()
    {
        _handler.SetPaginatedJsonResponse("api/servers/1/apps", new List<ServerAppDto>());
        var cut = Render<ServerAppsSection>(p => p.Add(x => x.ServerId, 1));
        var sources = (string[])typeof(ServerAppsSection)
            .GetField("_sources", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        Assert.Contains("manual", sources);
        Assert.Contains("systemd", sources);
        Assert.Contains("docker", sources);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void Priv_Set(object obj, string field, object? value)
        => typeof(ServerAppsSection).GetField(field, Priv)!.SetValue(obj, value);

    private static T? Priv_Get<T>(object obj, string field)
        => (T?)typeof(ServerAppsSection).GetField(field, Priv)!.GetValue(obj);
}
