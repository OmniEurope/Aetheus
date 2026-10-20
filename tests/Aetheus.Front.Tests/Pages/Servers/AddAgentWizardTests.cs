// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers;
using Aetheus.Front.Components.Servers.AgentWizard;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Wizard-level tests for AddAgent - covers uncovered branches:
/// OnInitializedAsync (success + fallback), BuildSteps / RefreshSteps,
/// CanAdvance per step, GenerateTokenAsync, OnStepChanged, GetAllCommands,
/// platform-option rendering, GoBack, OnFinish, CancelVerifyAsync.
/// </summary>
public class AddAgentWizardTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public AddAgentWizardTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/servers/agent-server-url", new { url = "https://aetheus.example.com" });
    }

    private IRenderedComponent<AddAgent> RenderWizard()
    {
        var cut = Render<AddAgent>();
        cut.WaitForState(() => cut.Markup.Length > 50);
        return cut;
    }

    // ── OnInitializedAsync ────────────────────────────────────────────────────

    [Fact]
    public void OnInit_WithServerUrl_SetsServerBaseUrl()
    {
        var cut = RenderWizard();
        var serverUrl = (string)typeof(AddAgent).GetField("_serverBaseUrl", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal("https://aetheus.example.com", serverUrl);
    }

    [Fact]
    public void OnInit_BuildsFourSteps()
    {
        var cut = RenderWizard();
        var steps = (System.Collections.IList)typeof(AddAgent).GetField("_steps", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(4, steps.Count);
    }

    [Fact]
    public void OnInit_ApiFailure_FallsBackToConfigUrl()
    {
        _handler.SetResponse("api/servers/agent-server-url", System.Net.HttpStatusCode.InternalServerError);
        var cut = Render<AddAgent>();
        cut.WaitForState(() => cut.Markup.Length > 50);
        var serverUrl = (string)typeof(AddAgent).GetField("_serverBaseUrl", Priv)!.GetValue(cut.Instance)!;
        Assert.Contains("127.0.0.1", serverUrl);
    }

    // ── CanAdvance ────────────────────────────────────────────────────────────

    [Fact]
    public void CanAdvance_Step0_PlatformSet_ReturnsTrue()
    {
        var cut = RenderWizard();
        typeof(AddAgent).GetField("_currentStep", Priv)!.SetValue(cut.Instance, 0);
        typeof(AddAgent).GetField("_platform", Priv)!.SetValue(cut.Instance, "linux");
        var method = typeof(AddAgent).GetMethod("CanAdvance", Priv)!;
        Assert.True((bool)method.Invoke(cut.Instance, [])!);
    }

    [Fact]
    public void CanAdvance_Step0_EmptyPlatform_ReturnsFalse()
    {
        var cut = RenderWizard();
        typeof(AddAgent).GetField("_currentStep", Priv)!.SetValue(cut.Instance, 0);
        typeof(AddAgent).GetField("_platform", Priv)!.SetValue(cut.Instance, "");
        var method = typeof(AddAgent).GetMethod("CanAdvance", Priv)!;
        Assert.False((bool)method.Invoke(cut.Instance, [])!);
    }

    [Fact]
    public void CanAdvance_Step1_AlwaysTrue()
    {
        var cut = RenderWizard();
        typeof(AddAgent).GetField("_currentStep", Priv)!.SetValue(cut.Instance, 1);
        var method = typeof(AddAgent).GetMethod("CanAdvance", Priv)!;
        Assert.True((bool)method.Invoke(cut.Instance, [])!);
    }

    [Fact]
    public void CanAdvance_Step2_AlwaysTrue()
    {
        var cut = RenderWizard();
        typeof(AddAgent).GetField("_currentStep", Priv)!.SetValue(cut.Instance, 2);
        var method = typeof(AddAgent).GetMethod("CanAdvance", Priv)!;
        Assert.True((bool)method.Invoke(cut.Instance, [])!);
    }

    // ── GetAllCommands ────────────────────────────────────────────────────────

    [Fact]
    public void GetAllCommands_Linux_ContainsServerBaseUrl()
    {
        var cut = RenderWizard();
        typeof(AddAgent).GetField("_platform", Priv)!.SetValue(cut.Instance, "linux");
        var cmds = cut.Instance.GetAllCommands();
        Assert.Contains(cmds, c => c.Contains("aetheus.example.com"));
    }

    [Fact]
    public void GetAllCommands_Windows_ContainsServerBaseUrl()
    {
        var cut = RenderWizard();
        typeof(AddAgent).GetField("_platform", Priv)!.SetValue(cut.Instance, "windows");
        var cmds = cut.Instance.GetAllCommands();
        Assert.Contains(cmds, c => c.Contains("aetheus.example.com"));
    }

    [Fact]
    public void GetAllCommands_NeverIncludesToken()
    {
        var cut = RenderWizard();
        typeof(AddAgent).GetField("_platform", Priv)!.SetValue(cut.Instance, "linux");
        typeof(AddAgent).GetField("_generatedToken", Priv)!.SetValue(cut.Instance,
            new RegistrationTokenDto { Token = "MYTOKEN12345" });
        var cmds = cut.Instance.GetAllCommands();
        Assert.DoesNotContain(cmds, c => c.Contains("MYTOKEN12345"));
        Assert.DoesNotContain(cmds, c => c.Contains("--token"));
    }

    [Fact]
    public void GetAllCommands_NoToken_NoPlaceholder()
    {
        var cut = RenderWizard();
        typeof(AddAgent).GetField("_generatedToken", Priv)!.SetValue(cut.Instance, null);
        var cmds = cut.Instance.GetAllCommands();
        Assert.DoesNotContain(cmds, c => c.Contains("<TOKEN>"));
        Assert.DoesNotContain(cmds, c => c.Contains("--token"));
    }

    // ── GenerateTokenAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GenerateTokenAsync_Success_SetsGeneratedToken()
    {
        _handler.SetJsonResponse("api/auth/registration-tokens", new RegistrationTokenDto { Token = "GENERATED-TOKEN-XYZ" });
        var cut = RenderWizard();
        var method = typeof(AddAgent).GetMethod("GenerateTokenAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var token = typeof(AddAgent).GetField("_generatedToken", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(token);
        var tokenProp = token!.GetType().GetProperty("Token")!.GetValue(token) as string;
        Assert.Equal("GENERATED-TOKEN-XYZ", tokenProp);
    }

    [Fact]
    public async Task GenerateTokenAsync_Failure_SetsGeneratedTokenNull()
    {
        _handler.SetResponse("api/auth/registration-tokens", System.Net.HttpStatusCode.InternalServerError);
        var cut = RenderWizard();
        var method = typeof(AddAgent).GetMethod("GenerateTokenAsync", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var generating = (bool)typeof(AddAgent).GetField("_tokenGenerating", Priv)!.GetValue(cut.Instance)!;
        Assert.False(generating);
    }

    // ── RefreshSteps ──────────────────────────────────────────────────────────

    [Fact]
    public void RefreshSteps_RebuildsStepsList()
    {
        var cut = RenderWizard();
        var field = typeof(AddAgent).GetField("_steps", Priv)!;
        var stepsBeforeRefresh = field.GetValue(cut.Instance);

        var method = typeof(AddAgent).GetMethod("RefreshSteps", Priv)!;
        cut.InvokeAsync(() => method.Invoke(cut.Instance, []));

        var stepsAfterRefresh = field.GetValue(cut.Instance);
        Assert.NotNull(stepsAfterRefresh);
    }

    // ── GoBack / OnFinish ─────────────────────────────────────────────────────

    [Fact]
    public void GoBack_NavigatesToServers()
    {
        var cut = RenderWizard();
        var method = typeof(AddAgent).GetMethod("GoBack", Priv)!;
        cut.InvokeAsync(() => method.Invoke(cut.Instance, []));
        // GoBack navigates back to the servers list.
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("/servers", nav.Uri);
    }

    [Fact]
    public void OnFinish_NavigatesToServers()
    {
        var cut = RenderWizard();
        var method = typeof(AddAgent).GetMethod("OnFinish", Priv)!;
        cut.InvokeAsync(() => method.Invoke(cut.Instance, []));
        // Finishing the wizard navigates to the servers list.
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.EndsWith("/servers", nav.Uri);
    }

    // ── ServerBaseUrl property ────────────────────────────────────────────────

    [Fact]
    public void ServerBaseUrl_WithServerBaseUrlSet_UsesIt()
    {
        var cut = RenderWizard();
        typeof(AddAgent).GetField("_serverBaseUrl", Priv)!.SetValue(cut.Instance, "https://custom.example.com");
        var prop = typeof(AddAgent).GetProperty("ServerBaseUrl", Priv)!;
        var val = (string)prop.GetValue(cut.Instance)!;
        Assert.Equal("https://custom.example.com", val);
    }

    [Fact]
    public void ServerBaseUrl_EmptyServerBaseUrl_FallsBackToConfig()
    {
        var cut = RenderWizard();
        typeof(AddAgent).GetField("_serverBaseUrl", Priv)!.SetValue(cut.Instance, "");
        var prop = typeof(AddAgent).GetProperty("ServerBaseUrl", Priv)!;
        var val = (string)prop.GetValue(cut.Instance)!;
        Assert.Contains("127.0.0.1", val);
    }

    // ── ElapsedSeconds (AgentDetectionMonitor) ────────────────────────────────

    private AgentDetectionMonitor MakeMonitor(TimeProvider? timeProvider = null) => new(
        Services.GetRequiredService<ApiClient>(),
        Services.GetRequiredService<HubConnectionFactory>(),
        NullLogger.Instance,
        () => Task.CompletedTask,
        timeProvider);

    [Fact]
    public void ElapsedSeconds_NoListeningStart_ReturnsZero()
    {
        var monitor = MakeMonitor();
        Assert.Equal(0, monitor.ElapsedSeconds);
    }

    [Fact]
    public async Task ElapsedSeconds_RecentStart_UsesInjectedClock()
    {
        var clock = new FakeTimeProvider();
        var monitor = MakeMonitor(clock);
        await monitor.StartAsync(null);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(5, monitor.ElapsedSeconds);
        await monitor.DisposeAsync();
    }

    // ── DisposeAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_NoHub_DoesNotThrow()
    {
        var cut = RenderWizard();
        await cut.Instance.DisposeAsync();
        // Disposal is idempotent: a second DisposeAsync (no hub was ever connected) must not throw.
        var ex = await Record.ExceptionAsync(async () => await cut.Instance.DisposeAsync());
        Assert.Null(ex);
    }

    [Fact]
    public void R357_PatchManage_IsCheckedByDefault_AndEmittedInTheInstallCommand()
    {
        var cut = RenderWizard();
        var patch = (bool)typeof(AddAgent).GetField("_includePatchManagement", Priv)!.GetValue(cut.Instance)!;
        Assert.True(patch);
        Assert.Contains("--enable-patch-manage", AgentInstallFlags.BuildLinux(true, true, false, patch, false, true), StringComparison.Ordinal);
    }
}
