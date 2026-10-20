// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Deep coverage for ServerAppsSection.razor.cs - AddAppAsync,
/// GetAppStatusBadge all status values, _addVisible panel render,
/// add-form fields, _sources list, no-apps empty state.
/// DeleteAppAsync excluded (Dialog.Confirm hangs).
/// </summary>
public class ServerAppsSectionDeepTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    public ServerAppsSectionDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static List<ServerAppDto> BuildApps(int count = 3) =>
        Enumerable.Range(1, count).Select(i => new ServerAppDto
        {
            Id = i,
            Name = $"app-{i}",
            Version = $"1.{i}.0",
            Port = 8000 + i,
            Source = i % 2 == 0 ? "docker" : "systemd",
            Status = i switch
            {
                1 => ServerAppStatus.Running,
                2 => ServerAppStatus.Stopped,
                3 => ServerAppStatus.Error,
                _ => ServerAppStatus.Unknown
            }
        }).ToList();

    private IRenderedComponent<ServerAppsSection> RenderSection(int serverId = 1,
        List<ServerAppDto>? apps = null)
    {
        // GET returns the list; the create POST to the same URL returns the created DTO (method-aware
        // stub wins for POST) so AddAppAsync's non-null result path runs.
        _handler.SetPaginatedJsonResponse($"api/servers/{serverId}/apps", apps ?? BuildApps());
        _handler.SetJsonResponse(HttpMethod.Post, $"api/servers/{serverId}/apps",
            new ServerAppDto { Id = 99, Name = "new-app" });
        return Render<ServerAppsSection>(p => p.Add(x => x.ServerId, serverId));
    }

    // ── Test 1: Renders apps list on init ─────────────────────────────────────

    [Fact]
    public void Render_WithApps_ShowsAppNames()
    {
        var cut = RenderSection(1);
        cut.WaitForState(
            () => typeof(ServerAppsSection).GetField("_apps", Priv)!.GetValue(cut.Instance) != null);

        Assert.Contains("app-1", cut.Markup);
        Assert.Contains("app-2", cut.Markup);
    }

    // ── Test 2: GetAppStatusBadge - Running ───────────────────────────────────

    [Fact]
    public void GetAppStatusBadge_Running_ReturnsSuccess()
    {
        var cut = RenderSection(2);
        var method = typeof(ServerAppsSection).GetMethod("GetAppStatusBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [ServerAppStatus.Running])!;
        Assert.Equal(OmniTone.Success, result);
    }

    // ── Test 3: GetAppStatusBadge - Stopped ──────────────────────────────────

    [Fact]
    public void GetAppStatusBadge_Stopped_ReturnsLight()
    {
        var cut = RenderSection(3);
        var method = typeof(ServerAppsSection).GetMethod("GetAppStatusBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [ServerAppStatus.Stopped])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    // ── Test 4: GetAppStatusBadge - Error ────────────────────────────────────

    [Fact]
    public void GetAppStatusBadge_Error_ReturnsDanger()
    {
        var cut = RenderSection(4);
        var method = typeof(ServerAppsSection).GetMethod("GetAppStatusBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [ServerAppStatus.Error])!;
        Assert.Equal(OmniTone.Danger, result);
    }

    // ── Test 5: GetAppStatusBadge - Unknown falls through to Warning ──────────

    [Fact]
    public void GetAppStatusBadge_Unknown_ReturnsWarning()
    {
        var cut = RenderSection(5);
        var method = typeof(ServerAppsSection).GetMethod("GetAppStatusBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [ServerAppStatus.Unknown])!;
        Assert.Equal(OmniTone.Warning, result);
    }

    // ── Test 6: _addVisible panel shows add form ─────────────────────────────

    [Fact]
    public void AddPanel_WhenVisible_ShowsForm()
    {
        var cut = RenderSection(6);
        cut.WaitForState(
            () => typeof(ServerAppsSection).GetField("_apps", Priv)!.GetValue(cut.Instance) != null);

        typeof(ServerAppsSection).GetField("_addVisible", Priv)!.SetValue(cut.Instance, true);
        cut.Render();

        // With _addVisible set, the add-application form card renders its heading.
        Assert.Contains("AddApplication", cut.Markup);
    }

    // ── Test 7: _sources static field contains expected values ───────────────

    [Fact]
    public void Sources_ContainsManualSystemdDocker()
    {
        var cut = RenderSection(7);
        var field = typeof(ServerAppsSection).GetField("_sources",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var sources = (string[])field.GetValue(null)!;
        Assert.Contains("manual", sources);
        Assert.Contains("systemd", sources);
        Assert.Contains("docker", sources);
    }

    // ── Test 8: AddAppAsync posts the create request and reloads the page ──

    [Fact]
    public async Task AddAppAsync_PostsCreateAndReloadsCurrentPage()
    {
        var cut = RenderSection(8);
        cut.WaitForState(
            () => typeof(ServerAppsSection).GetField("_apps", Priv)!.GetValue(cut.Instance) != null);

        typeof(ServerAppsSection).GetField("_addName", Priv)!.SetValue(cut.Instance, "my-new-app");
        typeof(ServerAppsSection).GetField("_addVersion", Priv)!.SetValue(cut.Instance, "1.0");
        typeof(ServerAppsSection).GetField("_addPort", Priv)!.SetValue(cut.Instance, (int?)9000);
        typeof(ServerAppsSection).GetField("_addSource", Priv)!.SetValue(cut.Instance, "docker");
        typeof(ServerAppsSection).GetField("_addVisible", Priv)!.SetValue(cut.Instance, true);

        var method = typeof(ServerAppsSection).GetMethod("AddAppAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        // The create is POSTed to the apps endpoint...
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/8/apps"));
        // ...the current server-side page is reloaded and the add panel closes.
        Assert.True(_handler.Requests.Count(r =>
            r.Method == "GET" && r.Url.Contains("api/servers/8/apps")) >= 2);
        Assert.False((bool)typeof(ServerAppsSection).GetField("_addVisible", Priv)!.GetValue(cut.Instance)!);
    }

    // ── Test 9: Empty apps list shows no-apps state ───────────────────────────

    [Fact]
    public void Render_EmptyApps_NoException()
    {
        var cut = RenderSection(9, []);
        cut.WaitForState(
            () => typeof(ServerAppsSection).GetField("_apps", Priv)!.GetValue(cut.Instance) != null);

        var apps = (List<ServerAppDto>?)typeof(ServerAppsSection).GetField("_apps", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(apps);
        Assert.Empty(apps);
    }

    // ── Test 10: AddAppAsync still creates when version is left empty ────────

    [Fact]
    public async Task AddAppAsync_EmptyVersion_StillPostsAndClearsForm()
    {
        var cut = RenderSection(10);
        cut.WaitForState(
            () => typeof(ServerAppsSection).GetField("_apps", Priv)!.GetValue(cut.Instance) != null);

        typeof(ServerAppsSection).GetField("_addName", Priv)!.SetValue(cut.Instance, "no-version-app");
        typeof(ServerAppsSection).GetField("_addVersion", Priv)!.SetValue(cut.Instance, "");

        var method = typeof(ServerAppsSection).GetMethod("AddAppAsync", Priv)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);

        // The empty-version branch (mapped to a null Version) must not block the create request.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/10/apps"));
        // On success the add form is reset (name cleared) - exercising the post-create cleanup path.
        var name = (string)typeof(ServerAppsSection).GetField("_addName", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(string.Empty, name);
    }

    // ── Test 11: _addSaving starts false ─────────────────────────────────────

    [Fact]
    public void AddSaving_InitialState_IsFalse()
    {
        var cut = RenderSection(11);

        var saving = (bool)typeof(ServerAppsSection).GetField("_addSaving", Priv)!.GetValue(cut.Instance)!;
        Assert.False(saving);
    }
}
