// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Exercises Apache section template branches with rich vhost/module data.
/// Targets the uncovered 29 lines in ServerApacheSection.
/// </summary>
public class ServerApacheSectionDeepTemplateTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerApacheSectionDeepTemplateTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ServerDetailDto MakeRichApacheServer() => new()
    {
        Id = 50,
        Name = "rich-apache",
        Hostname = "10.0.0.50",
        IpAddress = "10.0.0.50",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        CpuPercent = 15,
        MemoryUsedMb = 4096,
        MemoryTotalMb = 16384,
        DiskUsedGb = 30,
        DiskTotalGb = 200,
        Tags = [],
        Services = [],
        Docker = new DockerDataDto { Containers = [], Images = [], ComposeStacks = [], Networks = [], Volumes = [] },
        Apache = new ApacheDataDto
        {
            IsRunning = true,
            Version = "2.4.57",
            Pid = 1234,
            CollectionDegraded = true,
            CollectionDiagnostics = "sudo not available",
            Modules =
            [
                new ApacheModuleDto { Name = "mod_ssl_module", Type = "shared", IsEnabled = true },
                new ApacheModuleDto { Name = "mod_rewrite_module", Type = "shared", IsEnabled = false },
                new ApacheModuleDto { Name = "mod_authz_core_module", Type = "static", IsEnabled = true }
            ],
            VirtualHosts =
            [
                new ApacheVirtualHostDto { ServerName = "alpha.com", Port = 80, IsEnabled = true, DocumentRoot = "/var/www/alpha", ConfigFile = "alpha.conf" },
                new ApacheVirtualHostDto { ServerName = "alpha.com", Port = 443, IsEnabled = true, DocumentRoot = "/var/www/alpha", ConfigFile = "alpha-ssl.conf" },
                new ApacheVirtualHostDto { ServerName = "beta.com", Port = 80, IsEnabled = true, DocumentRoot = "/var/www/beta", ConfigFile = "beta.conf" },
                new ApacheVirtualHostDto { ServerName = "disabled.com", Port = 80, IsEnabled = false, DocumentRoot = "/var/www/disabled", ConfigFile = "disabled.conf" },
                new ApacheVirtualHostDto { ServerName = "disabled2.com", Port = 80, IsEnabled = false, DocumentRoot = "/var/www/disabled2", ConfigFile = "disabled2.conf" }
            ]
        }
    };

    private IRenderedComponent<ServerApacheSection> RenderSection()
    {
        _handler.SetJsonResponse("api/servers/50/apache/action", true);
        _handler.SetJsonResponse("api/servers/50/apache/logs", true);
        _handler.SetJsonResponse("api/servers/50/apache/config", "ServerName alpha.com");
        return Render<ServerApacheSection>(p => p
            .Add(x => x.Server, MakeRichApacheServer())
            .Add(x => x.ServerId, 50));
    }

    [Fact]
    public void Renders_RunningApache_WithoutVersionInTheStatusLine()
    {
        var cut = RenderSection();
        // R-120: the version is in the page title, not in the status line.
        Assert.DoesNotContain("2.4.57", cut.Markup);
    }

    [Fact]
    public void Renders_CollectionDegradedAlert()
    {
        var cut = RenderSection();
        Assert.Contains("ApacheCollectionDegraded", cut.Markup);
    }

    [Fact]
    public void EnabledVirtualHostGroups_MultipleEnabled_GroupsCorrectly()
    {
        var cut = RenderSection();
        var groups = cut.Instance.EnabledVirtualHostGroups;
        Assert.Equal(2, groups.Count); // alpha.com (merged) + beta.com
        var alpha = groups.FirstOrDefault(g => g.ServerName == "alpha.com");
        Assert.NotNull(alpha);
        Assert.Equal(2, alpha!.Members.Count);
    }

    [Fact]
    public void DisabledVirtualHostGroups_ReturnsAllDisabled()
    {
        var cut = RenderSection();
        var groups = cut.Instance.DisabledVirtualHostGroups;
        Assert.Equal(2, groups.Count); // disabled.com + disabled2.com
    }

    [Fact]
    public void Modules_StaticType_ExcludedFromSharedLogic()
    {
        var cut = RenderSection();
        var prop = typeof(ServerApacheSection).GetProperty("Apache", Priv)!;
        var modules = ((ApacheDataDto)prop.GetValue(cut.Instance)!).Modules;
        Assert.Equal(3, modules.Count);
        Assert.Contains(modules, m => m.Type == "static");
    }

    [Fact]
    public async Task ExecuteActionAsync_DisableSite_Completes()
    {
        var cut = RenderSection();
        var method = typeof(ServerApacheSection).GetMethod("ExecuteActionAsync", Priv)!;
        await (Task)method.Invoke(cut.Instance, [ApacheAction.DisableSite, "alpha.conf"])!;
        var running = (bool)typeof(ServerApacheSection).GetField("_actionRunning", Priv)!.GetValue(cut.Instance)!;
        Assert.False(running);
    }

    [Fact]
    public async Task ExecuteActionAsync_TestConfig_Completes()
    {
        var cut = RenderSection();
        var method = typeof(ServerApacheSection).GetMethod("ExecuteActionAsync", Priv)!;
        await (Task)method.Invoke(cut.Instance, [ApacheAction.TestConfig, null])!;
        // The TestConfig action is POSTed to the server's apache/action endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/50/apache/action"));
    }

    [Fact]
    public void ApacheVirtualHostGroup_DocumentRootsCompact_MultipleDistinctRoots()
    {
        var cut = RenderSection();
        // Create a group with two different doc roots
        var members = new List<ApacheVirtualHostDto>
        {
            new() { ServerName = "test.com", Port = 80, IsEnabled = true, DocumentRoot = "/var/www/http", ConfigFile = "test.conf" },
            new() { ServerName = "test.com", Port = 443, IsEnabled = true, DocumentRoot = "/var/www/https", ConfigFile = "test-ssl.conf" }
        };
        var group = new ServerApacheSection.ApacheVirtualHostGroup("test.com", members);
        Assert.Contains("/var/www/http", group.DocumentRootsCompact);
        Assert.Contains("/var/www/https", group.DocumentRootsCompact);
    }
}
