// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

public class ProjectServersSectionExtendedTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    public ProjectServersSectionExtendedTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ProjectServerDto MakeProjectServer(int id = 1, ProjectServerType type = ProjectServerType.AgentServer) =>
        new()
        {
            Id = id,
            ProjectId = 10,
            Type = type,
            DisplayName = $"Server {id}",
            Host = "10.0.0.1",
            Port = 22,
            ServerName = "agent-srv-1",
            ServerStatus = ServerStatus.Online
        };

    private void SetupEmptyServers()
    {
        _handler.SetPaginatedJsonResponse("api/projects/10/servers", new List<ProjectServerDto>());
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [],
            TotalCount = 0,
            Page = 1,
            PageSize = 200
        });
    }

    private void SetupWithServers()
    {
        _handler.SetPaginatedJsonResponse("api/projects/10/servers", new List<ProjectServerDto>
        {
            MakeProjectServer(1, ProjectServerType.AgentServer),
            MakeProjectServer(2, ProjectServerType.ExternalHost)
        });
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [],
            TotalCount = 0,
            Page = 1,
            PageSize = 200
        });
    }

    [Fact]
    public void Renders_EmptyServerList()
    {
        SetupEmptyServers();
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 10));
        cut.WaitForState(
            () => typeof(ProjectServersSection).GetField("_servers", Priv)!.GetValue(cut.Instance) != null);

        // OnInitializedAsync fetches the project servers; an empty API payload yields an empty list.
        Assert.Contains(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("api/projects/10/servers"));
        var servers = (List<ProjectServerDto>?)typeof(ProjectServersSection)
            .GetField("_servers", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(servers);
        Assert.Empty(servers);
    }

    [Fact]
    public void Renders_WithExistingServers()
    {
        SetupWithServers();
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 10));
        cut.WaitForState(() => cut.Markup.Contains("Server 1"));

        // The two seeded project servers are loaded and their display names rendered.
        Assert.Contains("Server 1", cut.Markup);
        Assert.Contains("Server 2", cut.Markup);
    }

    [Fact]
    public void ProjectIdChange_ReloadsServersOnSameInstance()
    {
        _handler.SetPaginatedJsonResponse("api/projects/10/servers", new[] { MakeProjectServer(1) });
        _handler.SetPaginatedJsonResponse("api/projects/20/servers", new[]
        {
            MakeProjectServer(2) with { ProjectId = 20, DisplayName = "Second project server" }
        });
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 10));

        cut.Render(p => p.Add(x => x.ProjectId, 20));

        cut.WaitForAssertion(() => Assert.Contains("Second project server", cut.Markup));
        Assert.DoesNotContain("Server 1", cut.Markup);
    }

    [Fact]
    public void GetTypeBadge_AgentServer_ReturnsInfo()
    {
        SetupEmptyServers();
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 10));

        var method = typeof(ProjectServersSection)
            .GetMethod("GetTypeBadge", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [ProjectServerType.AgentServer])!;
        Assert.Equal(OmniTone.Accent, result);
    }

    [Fact]
    public void GetTypeBadge_ExternalHost_ReturnsWarning()
    {
        SetupEmptyServers();
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 10));

        var method = typeof(ProjectServersSection)
            .GetMethod("GetTypeBadge", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [ProjectServerType.ExternalHost])!;
        Assert.Equal(OmniTone.Warning, result);
    }

    [Fact]
    public void GetTypeBadge_UnknownType_ReturnsLight()
    {
        SetupEmptyServers();
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 10));

        var method = typeof(ProjectServersSection)
            .GetMethod("GetTypeBadge", BindingFlags.NonPublic | BindingFlags.Static)!;
        // Pass an enum value outside known cases
        var result = (OmniTone)method.Invoke(null, [(ProjectServerType)99])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    [Fact]
    public void GetStatusBadge_Online_ReturnsSuccess()
    {
        SetupEmptyServers();
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 10));

        var method = typeof(ProjectServersSection)
            .GetMethod("GetStatusBadge", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [(ServerStatus?)ServerStatus.Online])!;
        Assert.Equal(OmniTone.Success, result);
    }

    [Fact]
    public void GetStatusBadge_Offline_ReturnsDanger()
    {
        SetupEmptyServers();
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 10));

        var method = typeof(ProjectServersSection)
            .GetMethod("GetStatusBadge", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [(ServerStatus?)ServerStatus.Offline])!;
        Assert.Equal(OmniTone.Danger, result);
    }

    [Fact]
    public void GetStatusBadge_Null_ReturnsLight()
    {
        SetupEmptyServers();
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 10));

        var method = typeof(ProjectServersSection)
            .GetMethod("GetStatusBadge", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [(ServerStatus?)null])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    [Fact]
    public async Task EnsureAgentServersAsync_LoadsOnce()
    {
        SetupWithServers();
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items =
            [
                new ServerDto { Id = 1, Name = "agent-1", Type = ServerType.Normal, Status = ServerStatus.Online, Hostname = "h1", Tags = [] }
            ],
            TotalCount = 1,
            Page = 1,
            PageSize = 200
        });

        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 10));

        var method = typeof(ProjectServersSection).GetMethod("EnsureAgentServersAsync", Priv)!;

        // Call twice - second call should be no-op
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var field = typeof(ProjectServersSection).GetField("_agentServers", Priv)!;
        var agentServers = (List<ServerDto>?)field.GetValue(cut.Instance);
        // First call loaded the single seeded agent server; the second short-circuits (already non-null),
        // so api/servers must have been fetched exactly once despite two invocations.
        Assert.NotNull(agentServers);
        Assert.Equal("agent-1", Assert.Single(agentServers).Name);
        Assert.Single(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("api/servers"));
    }

    [Fact]
    public void ServersField_NullBeforeLoad_ThenSetAfterInit()
    {
        SetupWithServers();
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 10));

        // After OnInitializedAsync, _servers should be set
        cut.WaitForState(() =>
        {
            var f = typeof(ProjectServersSection).GetField("_servers", Priv)!;
            return f.GetValue(cut.Instance) is not null;
        });

        var servers = (List<ProjectServerDto>?)typeof(ProjectServersSection)
            .GetField("_servers", Priv)!
            .GetValue(cut.Instance);
        // After OnInitializedAsync the field holds the two servers seeded by SetupWithServers.
        Assert.NotNull(servers);
        Assert.Equal(2, servers.Count);
    }

    [Fact]
    public async Task HeaderFilters_AreColumnFilters_NotASearchTerm()
    {
        // Send Type, Name, Host and Port as column filters to the project servers endpoint.
        SetupWithServers();
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 10));
        cut.WaitForState(() => cut.Markup.Contains("Server 1"));
        var grid = cut.FindComponent<AetheusDataGrid<ProjectServerDto>>();

        await cut.InvokeAsync(() => grid.Instance.LoadData.InvokeAsync(new GridLoadArgs
        {
            Filters =
            [
                new GridFilterDescriptor(nameof(ProjectServerDto.Type), "ExternalHost", OmniDataGridFilterOperator.In),
                new GridFilterDescriptor(nameof(ProjectServerDto.DisplayName), "web", OmniDataGridFilterOperator.Contains),
                new GridFilterDescriptor(nameof(ProjectServerDto.Host), "10.0", OmniDataGridFilterOperator.StartsWith),
                new GridFilterDescriptor(nameof(ProjectServerDto.Port), "22", OmniDataGridFilterOperator.Equals)
            ]
        }));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return url.Contains("api/projects/10/servers?", StringComparison.Ordinal)
                && url.Contains("Filters[0].Field=Type", StringComparison.Ordinal)
                && url.Contains("Filters[0].Value=ExternalHost", StringComparison.Ordinal)
                && url.Contains("Filters[1].Field=DisplayName", StringComparison.Ordinal)
                && url.Contains("Filters[2].Field=Host", StringComparison.Ordinal)
                && url.Contains("Filters[3].Field=Port", StringComparison.Ordinal)
                && url.Contains("Filters[3].Value=22", StringComparison.Ordinal)
                && !url.Contains("search=", StringComparison.Ordinal);
        }));
    }
}
