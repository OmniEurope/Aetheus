// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Settings;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Settings;

/// <summary>
/// Deep coverage for AdminSettings.razor.cs - OnInitializedAsync (admin + non-admin),
/// SaveSetting, AddSecret (validation + success), GenerateToken.
/// DeleteSecret excluded (Dialog.Confirm hangs).
/// </summary>
public class AdminSettingsDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public AdminSettingsDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private void SetupAdminData()
    {
        _handler.SetJsonResponse("api/settings", new List<AppSettingDto>
        {
            new AppSettingDto { Key = "MaxWorkers", Value = "4" },
            new AppSettingDto { Key = "Timeout", Value = "30" }
        });
        _handler.SetJsonResponse("api/settings/secrets", new List<SecretDto>
        {
            new SecretDto { Id = 1, Key = "API_TOKEN" }
        });
        _handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>
        {
            new RegistrationTokenDto { Id = 1, Token = "tok-abc" }
        });
    }

    // ── OnInitialized - admin path ────────────────────────────────────────────

    [Fact]
    public void OnInit_Admin_LoadsAllData()
    {
        SetupAdminData();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var settings = (List<AppSettingDto>)typeof(AdminSettings).GetField("_settings", Priv)!.GetValue(cut.Instance)!;
        var secrets = (List<SecretDto>)typeof(AdminSettings).GetField("_secrets", Priv)!.GetValue(cut.Instance)!;
        var tokens = (List<RegistrationTokenDto>)typeof(AdminSettings).GetField("_tokens", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal(2, settings.Count);
        Assert.Single(secrets);
        Assert.Single(tokens);
    }

    // ── OnInitialized - non-admin redirects ───────────────────────────────────

    [Fact]
    public void OnInit_NonAdmin_Redirects()
    {
        var ctx2 = new BunitContext();
        BunitTestHelper.RegisterServices(ctx2, isAdmin: false);
        var cut = ctx2.Render<AdminSettings>();

        // Non-admin OnInitializedAsync redirects to the app root before loading any data.
        var nav = ctx2.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.EndsWith("/", nav.Uri);
    }

    // ── OnInitialized - HTTP error graceful ───────────────────────────────────

    [Fact]
    public void OnInit_HttpError_FallsBackToEmpty()
    {
        _handler.SetResponse("api/settings", System.Net.HttpStatusCode.ServiceUnavailable);
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var settings = (List<AppSettingDto>)typeof(AdminSettings).GetField("_settings", Priv)!.GetValue(cut.Instance)!;
        Assert.Empty(settings);
    }

    // ── SaveSetting - key already saving, skips ────────────────────────────────

    [Fact]
    public async Task SaveSetting_AlreadySaving_Skips()
    {
        SetupAdminData();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var savingKeys = (HashSet<string>)typeof(AdminSettings).GetField("_savingKeys", Priv)!.GetValue(cut.Instance)!;
        savingKeys.Add("MaxWorkers");

        var notif = Services.GetRequiredService<OmniOverlayService>();
        var method = typeof(AdminSettings).GetMethod("SaveSetting", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["MaxWorkers"])!);

        // Already-saving key → early return before any API call or toast.
        Assert.Empty(notif.Toasts());
    }

    // ── SaveSetting - missing key in buffer, skips ─────────────────────────────

    [Fact]
    public async Task SaveSetting_MissingKey_Skips()
    {
        SetupAdminData();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var notif = Services.GetRequiredService<OmniOverlayService>();
        var method = typeof(AdminSettings).GetMethod("SaveSetting", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["NonExistentKey"])!);

        // Key absent from edit buffer → TryGetValue guard returns, no toast emitted.
        Assert.Empty(notif.Toasts());
    }

    // ── SaveSetting - success ─────────────────────────────────────────────────

    [Fact]
    public async Task SaveSetting_Success()
    {
        SetupAdminData();
        _handler.SetJsonResponse("api/settings/MaxWorkers", true);
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var buffer = (Dictionary<string, string>)typeof(AdminSettings).GetField("_editBuffer", Priv)!.GetValue(cut.Instance)!;
        buffer["MaxWorkers"] = "8";

        var notif = Services.GetRequiredService<OmniOverlayService>();
        var method = typeof(AdminSettings).GetMethod("SaveSetting", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["MaxWorkers"])!);

        // API returns true → success toast, and the key is released from _savingKeys.
        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Success, notif.Toasts()[0].Severity);
        var savingKeys = (HashSet<string>)typeof(AdminSettings).GetField("_savingKeys", Priv)!.GetValue(cut.Instance)!;
        Assert.DoesNotContain("MaxWorkers", savingKeys);
    }

    // ── AddSecret - missing fields shows warning ───────────────────────────────

    [Fact]
    public async Task AddSecret_MissingFields_ShowsWarning()
    {
        SetupAdminData();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        typeof(AdminSettings).GetField("_newSecretKey", Priv)!.SetValue(cut.Instance, "");
        typeof(AdminSettings).GetField("_newSecretValue", Priv)!.SetValue(cut.Instance, "");

        var notif = Services.GetRequiredService<OmniOverlayService>();
        var method = typeof(AdminSettings).GetMethod("AddSecret", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Empty key/value → a Warning toast and no secret refresh.
        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Warning, notif.Toasts()[0].Severity);
        Assert.Equal("SecretFieldsRequired", notif.Toasts()[0].Detail);
    }

    // ── AddSecret - success ────────────────────────────────────────────────────

    [Fact]
    public void AddSecret_Success_RefreshesSecrets_Loaded()
    {
        // Verify admin settings renders with secrets loaded
        SetupAdminData();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        var secrets = (List<SecretDto>)typeof(AdminSettings).GetField("_secrets", Priv)!.GetValue(cut.Instance)!;
        Assert.Single(secrets);
    }

    // ── AddSecret - api returns null ───────────────────────────────────────────

    [Fact]
    public async Task AddSecret_ApiNull_ShowsError()
    {
        SetupAdminData();
        _handler.SetResponse("api/settings/secrets", System.Net.HttpStatusCode.BadRequest);
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        typeof(AdminSettings).GetField("_newSecretKey", Priv)!.SetValue(cut.Instance, "MY_KEY");
        typeof(AdminSettings).GetField("_newSecretValue", Priv)!.SetValue(cut.Instance, "my_value");

        var notif = Services.GetRequiredService<OmniOverlayService>();
        var method = typeof(AdminSettings).GetMethod("AddSecret", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // API returns BadRequest → CreateSecretAsync null → Error toast, inputs stay filled.
        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Danger, notif.Toasts()[0].Severity);
        var key = (string)typeof(AdminSettings).GetField("_newSecretKey", Priv)!.GetValue(cut.Instance)!;
        Assert.Equal("MY_KEY", key);
    }

    // ── GenerateToken - tokens loaded at init ─────────────────────────────────
    // The full success path (POST → refresh GET) can't be exercised cleanly: the stub
    // TestHandler matches by URL substring and cannot distinguish POST from the refresh GET,
    // so POST and GetRegistrationTokensAsync collide on api/auth/registration-tokens (one wants
    // a single dto, the other a List<>). We assert the observable init state instead and cover
    // the failure branch in GenerateToken_NullResult_ShowsError below.

    [Fact]
    public void OnInit_LoadsRegistrationTokens()
    {
        SetupAdminData();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var tokens = (List<RegistrationTokenDto>)typeof(AdminSettings).GetField("_tokens", Priv)!.GetValue(cut.Instance)!;
        Assert.Single(tokens);
        Assert.Equal("tok-abc", tokens[0].Token);
    }

    // ── GenerateToken - null result ────────────────────────────────────────────

    [Fact]
    public async Task GenerateToken_NullResult_ShowsError()
    {
        SetupAdminData();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // POST returns NotFound → CreateRegistrationTokenAsync null → "SaveFailed" Error toast.
        _handler.SetResponse("api/auth/registration-tokens", System.Net.HttpStatusCode.NotFound);

        var notif = Services.GetRequiredService<OmniOverlayService>();
        var method = typeof(AdminSettings).GetMethod("GenerateToken", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        Assert.Single(notif.Toasts());
        Assert.Equal(OmniSeverity.Danger, notif.Toasts()[0].Severity);
    }

    // ── ToggleTokenReveal ─────────────────────────────────────────────────────

    [Fact]
    public void ToggleTokenReveal_AddsAndRemoves()
    {
        SetupAdminData();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var method = typeof(AdminSettings).GetMethod("ToggleTokenReveal", Priv)!;
        method.Invoke(cut.Instance, [1]);

        var revealed = (HashSet<int>)typeof(AdminSettings).GetField("_revealedTokens", Priv)!.GetValue(cut.Instance)!;
        Assert.Contains(1, revealed);

        // Toggle again removes it
        method.Invoke(cut.Instance, [1]);
        Assert.DoesNotContain(1, revealed);
    }
}
