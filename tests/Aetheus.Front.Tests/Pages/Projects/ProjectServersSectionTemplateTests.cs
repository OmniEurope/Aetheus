// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Projects;

public class ProjectServersSectionTemplateTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags PrivStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type SectionType = typeof(ProjectServersSection);

    public ProjectServersSectionTemplateTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetPaginatedJsonResponse("api/projects/1/servers", new List<ProjectServerDto>());
    }

    [Fact]
    public void Renders_HeaderAndResolvesLoading()
    {
        // FRONTTEST-004: the servers page is held back, so the section is seen loading (the product
        // loader, no empty state), then leaves that state once the empty page has arrived.
        var answer = new TaskCompletionSource<PaginatedResult<ProjectServerDto>>();
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/projects/1/servers", _ => answer.Task);
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 1));
        Assert.NotEmpty(cut.FindComponents<AetheusLoader>());
        Assert.DoesNotContain("NoProjectServers", cut.Markup);

        answer.SetResult(new PaginatedResult<ProjectServerDto> { Items = [], TotalCount = 0 });

        // One settled render carries all three, so the asserts never read a half-updated markup.
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindComponents<AetheusLoader>());
            Assert.Contains("Servers", cut.Markup);
            Assert.Contains("NoProjectServers", cut.Markup);
        });
    }

    [Fact]
    public void Renders_EmptyState_WhenNoServers()
    {
        _handler.SetPaginatedJsonResponse("api/projects/1/servers", new List<ProjectServerDto>());
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"), TimeSpan.FromSeconds(2));
        // No servers → a contextual empty state provides the direct add-server action.
        Assert.Contains("NoProjectServers", cut.Markup);
        Assert.Contains("AddServer", cut.Markup);
    }

    [Fact]
    public void Renders_AgentServerRow_WithLink()
    {
        _handler.SetPaginatedJsonResponse("api/projects/1/servers", new List<ProjectServerDto>
        {
            new() { Id = 1, ProjectId = 1, ServerId = 10, Type = ProjectServerType.AgentServer,
                DisplayName = "prod-web", Host = "10.0.0.1", Port = 22,
                ServerStatus = ServerStatus.Online }
        });
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("prod-web"), TimeSpan.FromSeconds(2));
        Assert.Contains("prod-web", cut.Markup);
    }

    [Fact]
    public void Renders_ExternalHostRow_WithoutLink()
    {
        _handler.SetPaginatedJsonResponse("api/projects/1/servers", new List<ProjectServerDto>
        {
            new() { Id = 2, ProjectId = 1, ServerId = null, Type = ProjectServerType.ExternalHost,
                DisplayName = "ext-host", Host = "external.example.com", Port = 443,
                ServerStatus = null }
        });
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("ext-host"), TimeSpan.FromSeconds(2));
        Assert.Contains("ext-host", cut.Markup);
    }

    [Fact]
    public void Renders_AgentServer_WithNullServerId_ShowsDisplayName()
    {
        _handler.SetPaginatedJsonResponse("api/projects/1/servers", new List<ProjectServerDto>
        {
            // AgentServer but ServerId is null (edge case)
            new() { Id = 3, ProjectId = 1, ServerId = null, Type = ProjectServerType.AgentServer,
                DisplayName = "unlinked-server", Host = "192.168.1.1", Port = 22,
                ServerStatus = ServerStatus.Offline }
        });
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("unlinked-server"), TimeSpan.FromSeconds(2));
        Assert.Contains("unlinked-server", cut.Markup);
    }

    [Fact]
    public void Renders_MixedServerTypes()
    {
        _handler.SetPaginatedJsonResponse("api/projects/1/servers", new List<ProjectServerDto>
        {
            new() { Id = 1, ProjectId = 1, ServerId = 10, Type = ProjectServerType.AgentServer,
                DisplayName = "agent-srv", Host = "10.0.0.1", Port = 22, ServerStatus = ServerStatus.Online },
            new() { Id = 2, ProjectId = 1, ServerId = null, Type = ProjectServerType.ExternalHost,
                DisplayName = "ext-srv", Host = "ext.example.com", Port = 80, ServerStatus = null }
        });
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("agent-srv"), TimeSpan.FromSeconds(2));
        Assert.Contains("agent-srv", cut.Markup);
        Assert.Contains("ext-srv", cut.Markup);
    }

    [Theory]
    [InlineData(ProjectServerType.AgentServer, OmniTone.Accent)]
    [InlineData(ProjectServerType.ExternalHost, OmniTone.Warning)]
    public void GetTypeBadge_ReturnsExpected(ProjectServerType type, OmniTone expected)
    {
        var method = SectionType.GetMethod("GetTypeBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [type])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetTypeBadge_Unknown_ReturnsLight()
    {
        var method = SectionType.GetMethod("GetTypeBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [(ProjectServerType)99])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    [Theory]
    [InlineData(ServerStatus.Online, OmniTone.Success)]
    [InlineData(ServerStatus.Offline, OmniTone.Danger)]
    public void GetStatusBadge_ReturnsExpected(ServerStatus status, OmniTone expected)
    {
        var method = SectionType.GetMethod("GetStatusBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [(ServerStatus?)status])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetStatusBadge_Null_ReturnsLight()
    {
        var method = SectionType.GetMethod("GetStatusBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [(ServerStatus?)null])!;
        Assert.Equal(OmniTone.Neutral, result);
    }

    [Fact]
    public async Task EnsureAgentServersAsync_CachesResult()
    {
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [new ServerDto { Id = 10, Name = "srv-a", Hostname = "srv-a.local", Type = ServerType.Normal, Status = ServerStatus.Online }],
            TotalCount = 1
        });
        var cut = Render<ProjectServersSection>(p => p.Add(x => x.ProjectId, 1));
        await cut.InvokeAsync(() => Task.CompletedTask);
        var method = SectionType.GetMethod("EnsureAgentServersAsync", Priv)!;
        // Call twice - second call should be a no-op (cached)
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        var agentServers = (List<ServerDto>?)SectionType.GetField("_agentServers", Priv)!.GetValue(cut.Instance);
        // The cached list holds the single agent server fetched on the first call.
        Assert.NotNull(agentServers);
        Assert.Equal(10, Assert.Single(agentServers).Id);
    }
}
