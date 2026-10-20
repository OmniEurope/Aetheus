// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Deep coverage for ProjectServersSection - GetTypeBadge, GetStatusBadge all paths,
/// rich server list render, EnsureAgentServers, and empty-state template branch.
/// OpenAddDialogAsync / OpenEditDialogAsync / DeleteServerAsync excluded (Dialog hangs).
/// </summary>
public class ProjectServersSectionDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectServersSectionDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private IRenderedComponent<ProjectServersSection> RenderWithServers(
        int projectId,
        List<ProjectServerDto>? servers = null)
    {
        _handler.SetPaginatedJsonResponse($"api/projects/{projectId}/servers",
            servers ?? BuildMixedServers(projectId));
        return Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, projectId));
    }

    private static List<ProjectServerDto> BuildMixedServers(int projectId) =>
    [
        new ProjectServerDto
        {
            Id = 1,
            ProjectId = projectId,
            Type = ProjectServerType.AgentServer,
            ServerId = 10,
            DisplayName = "web-01",
            Host = "10.0.0.1",
            Port = 22,
            ServerStatus = ServerStatus.Online
        },
        new ProjectServerDto
        {
            Id = 2,
            ProjectId = projectId,
            Type = ProjectServerType.AgentServer,
            ServerId = 11,
            DisplayName = "db-01",
            Host = "10.0.0.2",
            Port = 22,
            ServerStatus = ServerStatus.Offline
        },
        new ProjectServerDto
        {
            Id = 3,
            ProjectId = projectId,
            Type = ProjectServerType.ExternalHost,
            ServerId = null,
            DisplayName = "external-cdn",
            Host = "cdn.example.com",
            Port = 443
        }
    ];

    // ── Test 1: Renders with mixed server types ───────────────────────────────

    [Fact]
    public void Render_MixedServers_ShowsAllNames()
    {
        var cut = RenderWithServers(1);
        cut.WaitForState(
            () => typeof(ProjectServersSection).GetField("_servers", Priv)!.GetValue(cut.Instance) != null);

        Assert.Contains("web-01", cut.Markup);
        Assert.Contains("db-01", cut.Markup);
        Assert.Contains("external-cdn", cut.Markup);
    }

    // ── Test 2: GetTypeBadge - AgentServer ───────────────────────────────────

    [Fact]
    public void GetTypeBadge_AgentServer_ReturnsInfo()
    {
        var method = typeof(ProjectServersSection).GetMethod("GetTypeBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [ProjectServerType.AgentServer])!;
        Assert.Equal(OmniTone.Accent, result);
    }

    // ── Test 3: GetTypeBadge - ExternalHost ─────────────────────────────────

    [Fact]
    public void GetTypeBadge_ExternalHost_ReturnsWarning()
    {
        var method = typeof(ProjectServersSection).GetMethod("GetTypeBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [ProjectServerType.ExternalHost])!;
        Assert.Equal(OmniTone.Warning, result);
    }

    // ── Test 4: GetTypeBadge - default value ────────────────────────────────

    [Fact]
    public void GetTypeBadge_DefaultValue_ReturnsLight()
    {
        var method = typeof(ProjectServersSection).GetMethod("GetTypeBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [(ProjectServerType)99])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    // ── Test 5: GetStatusBadge - Online ─────────────────────────────────────

    [Fact]
    public void GetStatusBadge_Online_ReturnsSuccess()
    {
        var method = typeof(ProjectServersSection).GetMethod("GetStatusBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [(ServerStatus?)ServerStatus.Online])!;
        Assert.Equal(OmniTone.Success, result);
    }

    // ── Test 6: GetStatusBadge - Offline ────────────────────────────────────

    [Fact]
    public void GetStatusBadge_Offline_ReturnsDanger()
    {
        var method = typeof(ProjectServersSection).GetMethod("GetStatusBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [(ServerStatus?)ServerStatus.Offline])!;
        Assert.Equal(OmniTone.Danger, result);
    }

    // ── Test 7: GetStatusBadge - null ────────────────────────────────────────

    [Fact]
    public void GetStatusBadge_Null_ReturnsLight()
    {
        var method = typeof(ProjectServersSection).GetMethod("GetStatusBadge",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [(ServerStatus?)null])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    // ── Test 8: Empty server list shows empty state icon ─────────────────────

    [Fact]
    public void Render_EmptyServers_ShowsEmptyState()
    {
        var cut = RenderWithServers(2, []);
        cut.WaitForState(
            () => typeof(ProjectServersSection).GetField("_servers", Priv)!.GetValue(cut.Instance) != null);

        Assert.Contains("NoProjectServers", cut.Markup);
        Assert.Contains("AddServer", cut.Markup);
    }

    // ── Test 9: Empty server list from API shows empty state ─────────────────

    [Fact]
    public void Render_EmptyServersFromApi_ShowsComponent()
    {
        _handler.SetPaginatedJsonResponse("api/projects/300/servers", new List<ProjectServerDto>());
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 300));
        cut.WaitForState(
            () => typeof(ProjectServersSection).GetField("_servers", Priv)!.GetValue(cut.Instance) != null);

        // An empty API payload loads into an empty (non-null) _servers list and renders the empty state.
        var servers = (List<ProjectServerDto>?)typeof(ProjectServersSection)
            .GetField("_servers", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(servers);
        Assert.Empty(servers);
        Assert.Contains("NoProjectServers", cut.Markup);
        Assert.Contains("AddServer", cut.Markup);
    }

    // ── Test 10: EnsureAgentServersAsync loads servers when null ─────────────

    [Fact]
    public async Task EnsureAgentServersAsync_WhenNull_LoadsAgentServers()
    {
        _handler.SetPaginatedJsonResponse("api/projects/3/servers", new List<ProjectServerDto>());
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [new ServerDto { Id = 100, Name = "agent-srv", Type = ServerType.Normal }],
            TotalCount = 1
        });

        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 3));
        cut.WaitForState(
            () => typeof(ProjectServersSection).GetField("_servers", Priv)!.GetValue(cut.Instance) != null);

        var method = typeof(ProjectServersSection).GetMethod("EnsureAgentServersAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var agentServers = (List<ServerDto>?)typeof(ProjectServersSection)
            .GetField("_agentServers", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(agentServers);
        Assert.Single(agentServers);
    }

    // ── Test 11: EnsureAgentServersAsync is idempotent ───────────────────────

    [Fact]
    public async Task EnsureAgentServersAsync_WhenAlreadyLoaded_DoesNotReload()
    {
        _handler.SetPaginatedJsonResponse("api/projects/4/servers", new List<ProjectServerDto>());

        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 4));
        cut.WaitForState(
            () => typeof(ProjectServersSection).GetField("_servers", Priv)!.GetValue(cut.Instance) != null);

        // Pre-set _agentServers to a known value
        var existing = new List<ServerDto> { new() { Id = 200, Name = "pre-loaded" } };
        typeof(ProjectServersSection).GetField("_agentServers", Priv)!.SetValue(cut.Instance, existing);

        var method = typeof(ProjectServersSection).GetMethod("EnsureAgentServersAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Should still be the same pre-loaded list
        var agentServers = (List<ServerDto>?)typeof(ProjectServersSection)
            .GetField("_agentServers", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(agentServers);
        Assert.Equal("pre-loaded", agentServers[0].Name);
    }

    // ── Test 12: AgentServer with link rendered in markup ────────────────────

    [Fact]
    public void Render_AgentServerWithServerId_ShowsLink()
    {
        var cut = RenderWithServers(5);
        cut.WaitForState(
            () => typeof(ProjectServersSection).GetField("_servers", Priv)!.GetValue(cut.Instance) != null);

        // Agent server with ServerId should have a link to /servers/{ServerId}/overview
        Assert.Contains("/servers/10/overview", cut.Markup);
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        var cut = RenderWithServers(1);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)));

        // Per server: the server link, the edit and the delete buttons.
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 3);
    }
}
