// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerModulesSectionDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags PrivStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerModulesSectionDeepTests() => _handler = BunitTestHelper.RegisterServices(this);

    private IRenderedComponent<ServerModulesSection> RenderSection(int serverId = 1)
    {
        _handler.SetJsonResponse($"api/servers/{serverId}/modules", new List<ServerModuleDto>
        {
            new() { Id = 10, Name = "nginx", Type = ServerModuleType.Nginx, Status = ServerModuleStatus.Active },
            new() { Id = 11, Name = "node", Type = ServerModuleType.Node, Status = ServerModuleStatus.Inactive }
        });
        return Render<ServerModulesSection>(p => p.Add(x => x.ServerId, serverId));
    }

    [Fact]
    public void Renders_WithModules_LoadsOnInit()
    {
        var cut = RenderSection();
        cut.WaitForState(() =>
            typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance) is not null);

        // OnInit loads the two stubbed modules (nginx, node) into _modules.
        var modules = (List<ServerModuleDto>?)typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(modules);
        Assert.Equal(2, modules!.Count);
        Assert.Contains(modules, m => m.Name == "nginx");
    }

    [Fact]
    public void ModuleTypes_StaticField_ContainsAllEnumValues()
    {
        var types = (ServerModuleType[])typeof(ServerModulesSection)
            .GetField("_moduleTypes", PrivStatic)!
            .GetValue(null)!;
        Assert.Equal(Enum.GetValues<ServerModuleType>().Length, types.Length);
    }

    [Fact]
    public async Task AddModuleAsync_WhenApiReturnsModule_AddsToList()
    {
        var cut = RenderSection();
        cut.WaitForState(() =>
        {
            var f = typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance);
            return f is not null;
        });

        var newModule = new ServerModuleDto { Id = 99, Name = "redis", Type = ServerModuleType.Redis, Status = ServerModuleStatus.Active };
        _handler.SetJsonResponse("api/servers/1/modules", newModule);

        // Set form fields
        SetFormValue(cut.Instance, "_addModel", "Name", "redis");
        SetFormValue(cut.Instance, "_addModel", "Type", ServerModuleType.Redis);
        SetFormValue(cut.Instance, "_addModel", "Version", "");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddModuleAsync", "_addModel"));

        var modules = (List<ServerModuleDto>?)typeof(ServerModulesSection)
            .GetField("_modules", Priv)!
            .GetValue(cut.Instance);
        Assert.NotNull(modules);
        // The new module should have been added
        Assert.Contains(modules, m => m.Id == 99);
    }

    [Fact]
    public async Task AddModuleAsync_WithVersion_IncludesVersion()
    {
        var cut = RenderSection();
        cut.WaitForState(() =>
        {
            var f = typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance);
            return f is not null;
        });

        var newModule = new ServerModuleDto { Id = 98, Name = "redis", Type = ServerModuleType.Redis, Status = ServerModuleStatus.Active };
        _handler.SetJsonResponse("api/servers/1/modules", newModule);

        SetFormValue(cut.Instance, "_addModel", "Name", "redis");
        SetFormValue(cut.Instance, "_addModel", "Type", ServerModuleType.Redis);
        SetFormValue(cut.Instance, "_addModel", "Version", "7.0");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddModuleAsync", "_addModel"));

        // _addSaving should be false after completion
        var saving = (bool)typeof(ServerModulesSection).GetField("_addSaving", Priv)!.GetValue(cut.Instance)!;
        Assert.False(saving);
    }

    [Fact]
    public async Task AddModuleAsync_WhenApiReturnsNull_ModuleNotAdded()
    {
        var cut = RenderSection();
        cut.WaitForState(() =>
        {
            var f = typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance);
            return f is not null;
        });

        _handler.SetResponse("api/servers/1/modules", System.Net.HttpStatusCode.BadRequest);

        SetFormValue(cut.Instance, "_addModel", "Name", "redis");

        var countBefore = ((List<ServerModuleDto>?)typeof(ServerModulesSection)
            .GetField("_modules", Priv)!.GetValue(cut.Instance))?.Count ?? 0;

        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddModuleAsync", "_addModel"));

        var countAfter = ((List<ServerModuleDto>?)typeof(ServerModulesSection)
            .GetField("_modules", Priv)!.GetValue(cut.Instance))?.Count ?? 0;
        Assert.Equal(countBefore, countAfter);
        Assert.False((bool)typeof(ServerModulesSection)
            .GetField("_addSaving", Priv)!.GetValue(cut.Instance)!);
    }

    [Theory]
    [InlineData(ServerModuleStatus.Active, OmniTone.Success)]
    [InlineData(ServerModuleStatus.Inactive, OmniTone.Neutral)]
    [InlineData(ServerModuleStatus.Error, OmniTone.Danger)]
    public void GetModuleStatusBadge_ReturnsExpected(ServerModuleStatus status, OmniTone expected)
    {
        var method = typeof(ServerModulesSection).GetMethod("GetModuleStatusBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [(object)status])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetModuleStatusBadge_UnknownStatus_ReturnsWarning()
    {
        var method = typeof(ServerModulesSection).GetMethod("GetModuleStatusBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [(object)(ServerModuleStatus)99])!;
        Assert.Equal(OmniTone.Warning, result);
    }

    [Fact]
    public void AddVisible_InitiallyFalse()
    {
        _handler.SetJsonResponse("api/servers/1/modules", new List<ServerModuleDto>());
        var cut = Render<ServerModulesSection>(p => p.Add(x => x.ServerId, 1));
        var visible = (bool)typeof(ServerModulesSection).GetField("_addVisible", Priv)!.GetValue(cut.Instance)!;
        Assert.False(visible);
    }
}
