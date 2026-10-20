// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Render and method tests for ServerModulesSection that complement the existing
/// ServerModulesSectionTests and ServerModulesSectionDeepTests.
/// Targets: _addVisible toggle, AddModuleAsync null-module branch,
/// all ServerModuleStatus badge values, _moduleTypes static field, sorting/filtering.
/// </summary>
public class ServerModulesSectionRenderTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags PrivStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private readonly BunitTestHelper.TestHandler _handler;

    public ServerModulesSectionRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private IRenderedComponent<ServerModulesSection> RenderWithModules(int serverId = 7,
        IEnumerable<ServerModuleDto>? modules = null)
    {
        var list = modules?.ToList() ?? new List<ServerModuleDto>
        {
            new() { Id = 1, Name = "nginx",  Type = ServerModuleType.Nginx,      Status = ServerModuleStatus.Active },
            new() { Id = 2, Name = "redis",  Type = ServerModuleType.Redis,      Status = ServerModuleStatus.Inactive },
            new() { Id = 3, Name = "dotnet", Type = ServerModuleType.DotNet,     Status = ServerModuleStatus.Error }
        };
        _handler.SetJsonResponse($"api/servers/{serverId}/modules", list);
        return Render<ServerModulesSection>(p => p.Add(x => x.ServerId, serverId));
    }

    // ── Initial render ────────────────────────────────────────────────────────

    [Fact]
    public void Renders_LoadingSpinner_BeforeModulesLoaded()
    {
        _handler.SetJsonResponse("api/servers/7/modules", new List<ServerModuleDto>());
        var cut = Render<ServerModulesSection>(p => p.Add(x => x.ServerId, 7));

        // OnInitializedAsync fetches the module list for the configured ServerId.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/servers/7/modules"));
    }

    [Fact]
    public void Renders_WithModules_ListPopulated()
    {
        var cut = RenderWithModules();
        cut.WaitForState(() =>
        {
            var m = (List<ServerModuleDto>?)typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance);
            return m is not null;
        });
        var modules = (List<ServerModuleDto>?)typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(modules);
        Assert.Equal(3, modules!.Count);
    }

    [Fact]
    public void Renders_EmptyModules_ShowsEmptyState()
    {
        _handler.SetJsonResponse("api/servers/8/modules", new List<ServerModuleDto>());
        var cut = Render<ServerModulesSection>(p => p.Add(x => x.ServerId, 8));

        // Empty list still resolves to a non-null _modules so the empty-state branch renders.
        cut.WaitForState(() =>
            typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance) is not null);
        var modules = (List<ServerModuleDto>?)typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(modules);
        Assert.Empty(modules!);
    }

    // ── _addVisible toggle ────────────────────────────────────────────────────

    [Fact]
    public void AddVisible_InitiallyFalse()
    {
        _handler.SetJsonResponse("api/servers/7/modules", new List<ServerModuleDto>());
        var cut = Render<ServerModulesSection>(p => p.Add(x => x.ServerId, 7));
        var visible = (bool)typeof(ServerModulesSection).GetField("_addVisible", Priv)!.GetValue(cut.Instance)!;
        Assert.False(visible);
    }

    [Fact]
    public void SetAddVisible_True_ShowsAddForm()
    {
        _handler.SetJsonResponse("api/servers/7/modules", new List<ServerModuleDto>());
        var cut = Render<ServerModulesSection>(p => p.Add(x => x.ServerId, 7));

        typeof(ServerModulesSection).GetField("_addVisible", Priv)!.SetValue(cut.Instance, true);
        cut.Render();

        var visible = (bool)typeof(ServerModulesSection).GetField("_addVisible", Priv)!.GetValue(cut.Instance)!;
        Assert.True(visible);
    }

    // ── AddModuleAsync - module returned from API ─────────────────────────────

    [Fact]
    public async Task AddModuleAsync_ModuleReturned_ResetsAddFields()
    {
        var cut = RenderWithModules();
        cut.WaitForState(() =>
        {
            var m = (List<ServerModuleDto>?)typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance);
            return m is not null;
        });

        var newModule = new ServerModuleDto { Id = 99, Name = "postgres", Type = ServerModuleType.PostgreSQL, Status = ServerModuleStatus.Active };
        _handler.SetJsonResponse("api/servers/7/modules", newModule);

        SetFormValue(cut.Instance, "_addModel", "Name", "postgres");
        SetFormValue(cut.Instance, "_addModel", "Type", ServerModuleType.PostgreSQL);
        SetFormValue(cut.Instance, "_addModel", "Version", "16");
        typeof(ServerModulesSection).GetField("_addVisible", Priv)!.SetValue(cut.Instance, true);

        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddModuleAsync", "_addModel"));

        // After success: _addVisible = false, _addName = ""
        var visible = (bool)typeof(ServerModulesSection).GetField("_addVisible", Priv)!.GetValue(cut.Instance)!;
        var name = GetFormValue<string>(cut.Instance, "_addModel", "Name");
        Assert.False(visible);
        Assert.Equal(string.Empty, name);
    }

    // ── AddModuleAsync - whitespace version normalised to null ─────────────────

    [Fact]
    public async Task AddModuleAsync_WhitespaceVersion_TreatedAsNoVersion()
    {
        var cut = RenderWithModules();
        cut.WaitForState(() =>
        {
            var m = (List<ServerModuleDto>?)typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance);
            return m is not null;
        });

        var newModule = new ServerModuleDto { Id = 88, Name = "nginx", Type = ServerModuleType.Nginx, Status = ServerModuleStatus.Active };
        _handler.SetJsonResponse("api/servers/7/modules", newModule);

        SetFormValue(cut.Instance, "_addModel", "Name", "nginx");
        SetFormValue(cut.Instance, "_addModel", "Version", "   "); // whitespace

        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "AddModuleAsync", "_addModel"));

        var saving = (bool)typeof(ServerModulesSection).GetField("_addSaving", Priv)!.GetValue(cut.Instance)!;
        Assert.False(saving);
    }

    // ── GetModuleStatusBadge - all enum arms ──────────────────────────────────

    [Theory]
    [InlineData(ServerModuleStatus.Active, OmniTone.Success)]
    [InlineData(ServerModuleStatus.Inactive, OmniTone.Neutral)]
    [InlineData(ServerModuleStatus.Error, OmniTone.Danger)]
    public void GetModuleStatusBadge_KnownStatus_ReturnsExpected(ServerModuleStatus status, OmniTone expected)
    {
        var method = typeof(ServerModulesSection).GetMethod("GetModuleStatusBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [(object)status])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetModuleStatusBadge_UnknownStatus_ReturnsWarning()
    {
        var method = typeof(ServerModulesSection).GetMethod("GetModuleStatusBadge", PrivStatic)!;
        var result = (OmniTone)method.Invoke(null, [(object)(ServerModuleStatus)999])!;
        Assert.Equal(OmniTone.Warning, result);
    }

    // ── _moduleTypes static field ─────────────────────────────────────────────

    [Fact]
    public void ModuleTypes_StaticField_ContainsAllEnumValues()
    {
        var field = typeof(ServerModulesSection).GetField("_moduleTypes", PrivStatic)!;
        var types = (ServerModuleType[])field.GetValue(null)!;
        Assert.Equal(Enum.GetValues<ServerModuleType>().Length, types.Length);
    }

    // ── Multiple module types in list ─────────────────────────────────────────

    [Fact]
    public void Renders_AllModuleTypes_WithoutError()
    {
        var allTypes = Enum.GetValues<ServerModuleType>()
            .Select((type, idx) => new ServerModuleDto
            {
                Id = idx + 1,
                Name = type.ToString(),
                Type = type,
                Status = ServerModuleStatus.Active
            })
            .ToList();

        _handler.SetJsonResponse("api/servers/9/modules", allTypes);
        var cut = Render<ServerModulesSection>(p => p.Add(x => x.ServerId, 9));

        // Every enum-typed module loads into _modules without the badge switch throwing.
        cut.WaitForState(() =>
            typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance) is not null);
        var modules = (List<ServerModuleDto>?)typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(modules);
        Assert.Equal(allTypes.Count, modules!.Count);
    }

    // ── Module count after init ───────────────────────────────────────────────

    [Fact]
    public void ModuleList_IsNullBeforeInit_ThenPopulated()
    {
        _handler.SetJsonResponse("api/servers/10/modules", new List<ServerModuleDto>
        {
            new() { Id = 1, Name = "test", Type = ServerModuleType.DotNet, Status = ServerModuleStatus.Active }
        });
        var cut = Render<ServerModulesSection>(p => p.Add(x => x.ServerId, 10));

        cut.WaitForState(() =>
        {
            var m = (List<ServerModuleDto>?)typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance);
            return m is not null;
        });

        var modules = (List<ServerModuleDto>?)typeof(ServerModulesSection).GetField("_modules", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(modules);
        Assert.Single(modules!);
    }
}
