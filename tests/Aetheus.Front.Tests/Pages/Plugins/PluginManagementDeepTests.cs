// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Plugins;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Plugins;

public class PluginManagementDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public PluginManagementDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private static List<PluginRegistrationDto> MakePlugins() =>
    [
        new() { Id = 1, Name = "AuditPlugin", Version = "1.0.0", Status = PluginStatus.Enabled, Type = PluginType.Collector },
        new() { Id = 2, Name = "DeployPlugin", Version = "2.0.1", Status = PluginStatus.Disabled, Type = PluginType.Executor },
        new() { Id = 3, Name = "BrokenPlugin", Version = "0.1.0", Status = PluginStatus.Error, Type = PluginType.Collector }
    ];

    private void SetupPlugins(List<PluginRegistrationDto>? plugins = null)
    {
        _handler.SetPaginatedJsonResponse("api/plugins", plugins ?? MakePlugins());
    }

    [Fact]
    public void Renders_PluginList()
    {
        SetupPlugins();
        var cut = Render<PluginManagement>();
        cut.WaitForState(() => cut.Markup.Contains("AuditPlugin") || !cut.Markup.Contains("rz-progressbar-circular"));

        // The three stubbed plugins are loaded from the API and rendered by name.
        var plugins = (List<PluginRegistrationDto>)typeof(PluginManagement).GetField("_plugins", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(3, plugins.Count);
        Assert.Contains("AuditPlugin", cut.Markup);
    }

    [Fact]
    public void Loading_FalseAfterInit()
    {
        SetupPlugins();
        var cut = Render<PluginManagement>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        var loading = (bool)typeof(PluginManagement).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
    }

    [Fact]
    public void Handles_HttpError_Gracefully()
    {
        _handler.SetResponse("api/plugins", System.Net.HttpStatusCode.InternalServerError);
        var cut = Render<PluginManagement>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        var plugins = (List<PluginRegistrationDto>)typeof(PluginManagement).GetField("_plugins", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(plugins);
    }

    // X4D8: the register form moved to PluginRegisterDialog; its validation/register behaviour is
    // covered by PluginRegisterDialogTests. The old inline-panel register test was removed here.

    [Fact]
    public async Task TogglePlugin_EnabledToDisabled_ReloadsPluginsFromApi()
    {
        SetupPlugins();
        // PUT api/plugins/1 succeeds; subsequent reload returns the plugin now Disabled.
        _handler.SetJsonResponse("api/plugins/1", new PluginRegistrationDto { Id = 1, Name = "AuditPlugin", Version = "1.0.0", Status = PluginStatus.Disabled, Type = PluginType.Collector });
        var cut = Render<PluginManagement>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // After the update, the list reload returns a single Disabled plugin (distinct from the
        // initial MakePlugins() list) - proving the success branch reloaded from the API.
        _handler.SetPaginatedJsonResponse("api/plugins",
            new List<PluginRegistrationDto> { new() { Id = 1, Name = "AuditPlugin", Version = "1.0.0", Status = PluginStatus.Disabled, Type = PluginType.Collector } });

        var plugin = new PluginRegistrationDto { Id = 1, Name = "AuditPlugin", Version = "1.0.0", Status = PluginStatus.Enabled, Type = PluginType.Collector };
        var method = typeof(PluginManagement).GetMethod("TogglePlugin", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [plugin])!);
        // R-10: the list is read again by the grid, quietly, behind the row the save answered.
        await cut.InvokeAsync(() => cut.FindComponent<AetheusDataGrid<PluginRegistrationDto>>().Instance.BackgroundRefresh);

        var plugins = (List<PluginRegistrationDto>)typeof(PluginManagement).GetField("_plugins", Priv)!.GetValue(cut.Instance)!;
        Assert.Single(plugins);
        Assert.Equal(PluginStatus.Disabled, plugins[0].Status);
    }

    [Theory]
    [InlineData(PluginStatus.Enabled, OmniTone.Success)]
    [InlineData(PluginStatus.Disabled, OmniTone.Warning)]
    [InlineData(PluginStatus.Error, OmniTone.Danger)]
    public void GetStatusBadgeStyle_ReturnsExpected(PluginStatus status, OmniTone expected)
    {
        var method = typeof(PluginManagement).GetMethod("GetStatusBadgeStyle", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [status])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NonAdmin_RedirectsHome_AndDoesNotLoadPlugins()
    {
        var auth = (AuthStateProvider)Services.GetService(typeof(AuthStateProvider))!;
        typeof(AuthStateProvider).GetProperty("Roles")!.SetValue(auth, new List<string>());
        Assert.False(auth.IsAdmin);
        // A non-empty stub: if the page wrongly loaded, _plugins would be populated.
        _handler.SetPaginatedJsonResponse("api/plugins", MakePlugins());

        var cut = Render<PluginManagement>();
        var injectedAuth = (AuthStateProvider)typeof(PluginManagement)
            .GetProperty("Auth", Priv)!.GetValue(cut.Instance)!;
        Assert.Same(auth, injectedAuth);
        Assert.False(injectedAuth.IsAdmin);

        // OnInitializedAsync returns early for non-admins BEFORE the load + before _loading=false.
        // The populated stub is therefore never consumed: the list stays empty and loading stays true.
        var plugins = (List<PluginRegistrationDto>)typeof(PluginManagement).GetField("_plugins", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(plugins);
        var loading = (bool)typeof(PluginManagement).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.True(loading, "non-admin guard returns before _loading is cleared");
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("api/plugins"));
    }
}
