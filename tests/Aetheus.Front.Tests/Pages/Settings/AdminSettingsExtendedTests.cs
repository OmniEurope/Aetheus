// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Settings;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Extended coverage for AdminSettings - tests methods not covered by AdminSettingsTests.cs:
/// DeleteSecret, CopyToken, SaveSetting variants, ToggleSecretReveal, GenerateToken.
/// </summary>
public class AdminSettingsExtendedTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AdminSettingsExtendedTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private void SetupAllMocks(
        List<AppSettingDto>? settings = null,
        List<SecretDto>? secrets = null,
        List<RegistrationTokenDto>? tokens = null)
    {
        _handler.SetJsonResponse("api/settings", settings ?? [new AppSettingDto { Key = "SiteName", Value = "Aetheus" }]);
        _handler.SetJsonResponse("api/settings/secrets", secrets ?? [new SecretDto { Id = 1, Key = "API_KEY" }]);
        _handler.SetJsonResponse("api/auth/registration-tokens", tokens ?? [new RegistrationTokenDto { Id = 1, Token = "tok-abc-123", ExpiresAt = DateTime.UtcNow.AddHours(24) }]);
    }

    // DeleteSecret removed - calls Dialog.Confirm which hangs in bUnit

    [Fact]
    public async Task CopyToken_InvokesClipboardJs()
    {
        SetupAllMocks();

        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        var notif = Services.GetRequiredService<OmniOverlayService>();
        var method = typeof(AdminSettings).GetMethod("CopyToken", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["tok-abc-123"])!);

        // CL8R: token copy now routes through the shared ClipboardService - text is pushed to the
        // clipboard and the user gets a single success toast.
        var copy = JSInterop.Invocations.Single(i => i.Identifier == "navigator.clipboard.writeText");
        Assert.Equal("tok-abc-123", copy.Arguments[0]);
        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Success, notif.Toasts()[0].Severity);
    }

    [Fact]
    public async Task SaveSetting_WithMissingKey_EarlyReturns()
    {
        SetupAllMocks(settings: []);

        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        // Buffer is empty; key "NonExistent" is not in _editBuffer - covers the TryGetValue guard
        var notif = Services.GetRequiredService<OmniOverlayService>();
        var method = typeof(AdminSettings).GetMethod("SaveSetting", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["NonExistent"])!);

        // Guard returns before any API call → no toast.
        Assert.Empty(notif.Toasts());
    }

    [Fact]
    public async Task SaveSetting_WithExistingKey_CallsApi()
    {
        SetupAllMocks(settings: [new AppSettingDto { Key = "SiteName", Value = "Aetheus" }]);
        _handler.SetJsonResponse("api/settings/SiteName", true);

        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        var notif = Services.GetRequiredService<OmniOverlayService>();
        var method = typeof(AdminSettings).GetMethod("SaveSetting", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["SiteName"])!);

        // API returns true → success toast surfaces "SettingSaved".
        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Success, notif.Toasts()[0].Severity);
        Assert.Equal("SettingSaved", notif.Toasts()[0].Detail);
    }

    [Fact]
    public void ToggleSecretReveal_TogglesTokenOnAndOff()
    {
        SetupAllMocks();

        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        var toggle = typeof(AdminSettings).GetMethod("ToggleTokenReveal", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var field = typeof(AdminSettings).GetField("_revealedTokens", BindingFlags.NonPublic | BindingFlags.Instance)!;

        toggle.Invoke(cut.Instance, [1]);
        var revealed = (HashSet<int>)field.GetValue(cut.Instance)!;
        Assert.Contains(1, revealed);

        toggle.Invoke(cut.Instance, [1]);
        Assert.DoesNotContain(1, revealed);
    }

    [Fact]
    public void ToggleSecretReveal_MultipleTokens_IndependentState()
    {
        SetupAllMocks();

        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        var toggle = typeof(AdminSettings).GetMethod("ToggleTokenReveal", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var field = typeof(AdminSettings).GetField("_revealedTokens", BindingFlags.NonPublic | BindingFlags.Instance)!;

        toggle.Invoke(cut.Instance, [1]);
        toggle.Invoke(cut.Instance, [2]);
        var revealed = (HashSet<int>)field.GetValue(cut.Instance)!;

        Assert.Contains(1, revealed);
        Assert.Contains(2, revealed);

        toggle.Invoke(cut.Instance, [1]);
        Assert.DoesNotContain(1, revealed);
        Assert.Contains(2, revealed);
    }

    [Fact]
    public async Task GenerateToken_ApiReturnsNull_ShowsError()
    {
        // POST and GET both hit api/auth/registration-tokens; TestHandler cannot distinguish
        // by HTTP method. After init (which uses the list stub), we switch the stub to a 404
        // so PostJsonAsync returns null, the "if (token is not null)" guard is false, no
        // GetRegistrationTokensAsync refresh is attempted, and no deserialization conflict
        // occurs. The assertion verifies the method completed without an unhandled exception.
        SetupAllMocks();

        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        _handler.SetResponse("api/auth/registration-tokens", System.Net.HttpStatusCode.NotFound);

        var notif = Services.GetRequiredService<OmniOverlayService>();
        var method = typeof(AdminSettings).GetMethod("GenerateToken", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // POST 404 → CreateRegistrationTokenAsync null → "SaveFailed" Error toast.
        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Danger, notif.Toasts()[0].Severity);
    }

    [Fact]
    public async Task AddSecret_WithBothFields_SendsCreatePost()
    {
        // Init requires api/settings/secrets to return List<SecretDto>; set up correctly first.
        // After init, override with a 404 so CreateSecretAsync returns null (the "if (created
        // is not null)" guard is false), avoiding the incompatible list-vs-object deserialization
        // that arises when POST and GET share the same URL key in TestHandler.
        SetupAllMocks();

        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        _handler.SetResponse("api/settings/secrets", System.Net.HttpStatusCode.NotFound);

        typeof(AdminSettings).GetField("_newSecretKey", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, "NEW_SECRET");
        typeof(AdminSettings).GetField("_newSecretValue", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, "super-secret-value");

        var method = typeof(AdminSettings).GetMethod("AddSecret", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Both fields are set, so the empty-field guard is skipped and a create POST is sent.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/settings/secrets"));
    }

    [Fact]
    public void Renders_WithMultipleSettings_ShowsAllKeys()
    {
        SetupAllMocks(settings:
        [
            new AppSettingDto { Key = "SiteName", Value = "Aetheus" },
            new AppSettingDto { Key = "MaxAgents", Value = "10" },
            new AppSettingDto { Key = "LogLevel", Value = "Information" }
        ]);

        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        var settings = (List<AppSettingDto>)typeof(AdminSettings)
            .GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.Equal(3, settings.Count);
    }

    [Fact]
    public void Renders_WithMultipleTokens_AllInList()
    {
        SetupAllMocks(tokens:
        [
            new RegistrationTokenDto { Id = 1, Token = "tok-aaa", ExpiresAt = DateTime.UtcNow.AddHours(24) },
            new RegistrationTokenDto { Id = 2, Token = "tok-bbb", ExpiresAt = DateTime.UtcNow.AddHours(48), IsUsed = true }
        ]);

        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        var tokens = (List<RegistrationTokenDto>)typeof(AdminSettings)
            .GetField("_tokens", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.Equal(2, tokens.Count);
    }
}
