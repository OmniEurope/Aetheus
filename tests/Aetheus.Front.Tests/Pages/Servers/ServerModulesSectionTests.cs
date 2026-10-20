// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerModulesSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerModulesSectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_EmptyList_ShowsEmptyText_WithoutTitleHeading()
    {
        _handler.SetJsonResponse("api/servers/1/modules", new List<ServerModuleDto>());
        var cut = Render<ServerModulesSection>(p => p.Add(x => x.ServerId, 1));
        cut.WaitForAssertion(() => Assert.Contains("NoRecords", cut.Markup));
        // The title heading is gone: the page header already shows it.
        Assert.DoesNotContain(">Modules<", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Renders_WithModules_ShowsModuleNamesAndTypeBadges()
    {
        _handler.SetJsonResponse("api/servers/1/modules", new List<ServerModuleDto>
        {
            new() { Id = 1, Name = ".NET SDK", Type = ServerModuleType.DotNet, Version = "10.0", Status = ServerModuleStatus.Active },
            new() { Id = 2, Name = "Docker", Type = ServerModuleType.Docker, Version = "24.0", Status = ServerModuleStatus.Active }
        });
        var cut = Render<ServerModulesSection>(p => p.Add(x => x.ServerId, 1));
        cut.WaitForAssertion(() => Assert.Contains(".NET SDK", cut.Markup));
        Assert.Contains("Docker", cut.Markup);
        // The Type column renders the enum name as a badge (template uses m.Type.ToString()).
        Assert.Contains("DotNet", cut.Markup);
        Assert.Contains("10.0", cut.Markup);
    }

    [Fact]
    public void ServerIdChange_ReloadsModulesOnSameInstance()
    {
        _handler.SetJsonResponse("api/servers/1/modules", new[]
        {
            new ServerModuleDto { Id = 1, Name = "first-module" }
        });
        _handler.SetJsonResponse("api/servers/2/modules", new[]
        {
            new ServerModuleDto { Id = 2, Name = "second-module" }
        });
        var cut = Render<ServerModulesSection>(p => p.Add(x => x.ServerId, 1));

        cut.Render(p => p.Add(x => x.ServerId, 2));

        cut.WaitForAssertion(() => Assert.Contains("second-module", cut.Markup));
        Assert.DoesNotContain("first-module", cut.Markup);
    }


    [Fact]
    public void Renders_ManyModules_ShowsAllRows()
    {
        _handler.SetJsonResponse("api/servers/1/modules", new List<ServerModuleDto>
        {
            new() { Id = 1, Name = ".NET", Type = ServerModuleType.DotNet, Version = "10.0" },
            new() { Id = 2, Name = "Docker", Type = ServerModuleType.Docker, Version = "24.0" },
            new() { Id = 3, Name = "Node.js", Type = ServerModuleType.Node, Version = "22.0" },
            new() { Id = 4, Name = "PostgreSQL", Type = ServerModuleType.PostgreSQL, Version = "16" },
            new() { Id = 5, Name = "Redis", Type = ServerModuleType.Redis, Version = "7.0" }
        });
        var cut = Render<ServerModulesSection>(p => p.Add(x => x.ServerId, 1));
        cut.WaitForAssertion(() => Assert.Contains("PostgreSQL", cut.Markup));
        Assert.Contains("Node.js", cut.Markup);
        Assert.Contains("Redis", cut.Markup);
    }
}
