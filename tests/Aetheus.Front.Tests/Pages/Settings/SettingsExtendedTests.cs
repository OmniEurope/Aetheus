// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Settings;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SettingsPage = Aetheus.Front.Components.Settings.Settings;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Extended coverage for <see cref="Settings"/> complementing <see cref="SettingsTests"/>:
/// tests OnTabChange navigation, TOTP setup/verify/disable flows, and tab-index initialisation.
/// </summary>
public class SettingsExtendedTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    private static readonly BindingFlags InstPriv = BindingFlags.NonPublic | BindingFlags.Instance;

    private static UserDto AuthenticatedUser() => new()
    {
        Id = 1,
        Username = "testuser",
        Email = "test@example.com",
        IsActive = true,
        TotpEnabled = false
    };

    private static TotpSetupResponse TotpSetup() => new()
    {
        SharedKey = "JBSWY3DPEHPK3PXP",
        AuthenticatorUri = "otpauth://totp/Aetheus:testuser?secret=JBSWY3DPEHPK3PXP",
        RecoveryCodes = ["1111-2222", "3333-4444"]
    };

    public SettingsExtendedTests() => _handler = BunitTestHelper.RegisterServices(this);

    private void StubDefaults()
    {
        _handler.SetJsonResponse("api/users/me", AuthenticatedUser());
    }

    // ── Render ────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_SettingsPage()
    {
        StubDefaults();
        var cut = Render<SettingsPage>();
        cut.WaitForState(() => cut.Markup.Length > 50);
        Assert.Contains("Settings", cut.Markup);
    }

    [Fact]
    public void Renders_WithTabParameter_SetsCorrectTabIndex()
    {
        StubDefaults();
        var cut = Render<SettingsPage>(p => p.Add(x => x.Tab, "security"));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var idx = (int)typeof(SettingsPage)
            .GetField("_selectedTabIndex", InstPriv)!
            .GetValue(cut.Instance)!;
        Assert.Equal(2, idx); // "security" is index 2 in TabNames
    }

    [Fact]
    public void Renders_WithUnknownTab_FallsBackToZero()
    {
        StubDefaults();
        var cut = Render<SettingsPage>(p => p.Add(x => x.Tab, "nonexistent"));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var idx = (int)typeof(SettingsPage)
            .GetField("_selectedTabIndex", InstPriv)!
            .GetValue(cut.Instance)!;
        Assert.Equal(0, idx);
    }

    // ── OnTabChange ───────────────────────────────────────────────────────────

    [Fact]
    public void OnTabChange_UpdatesSelectedTabIndex()
    {
        StubDefaults();
        var cut = Render<SettingsPage>();
        var method = typeof(SettingsPage).GetMethod("OnTabChange", InstPriv)!;

        method.Invoke(cut.Instance, ["security"]); // OE tabs report their key; "security" is index 2

        var idx = (int)typeof(SettingsPage)
            .GetField("_selectedTabIndex", InstPriv)!
            .GetValue(cut.Instance)!;
        Assert.Equal(2, idx);
    }

    [Fact]
    public void OnTabChange_OutOfRange_DoesNotNavigate()
    {
        StubDefaults();
        var cut = Render<SettingsPage>();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var method = typeof(SettingsPage).GetMethod("OnTabChange", InstPriv)!;
        var uriBefore = nav.Uri;

        // A key that names no tab has no index: nothing is stored and no /settings/{tab}
        // navigation happens.
        method.Invoke(cut.Instance, ["unknown"]);

        var idx = (int)typeof(SettingsPage)
            .GetField("_selectedTabIndex", InstPriv)!
            .GetValue(cut.Instance)!;
        Assert.Equal(0, idx);
        // No /settings/{tab} navigation happened - the URL is unchanged.
        Assert.Equal(uriBefore, nav.Uri);
        Assert.DoesNotContain("/settings/", nav.Uri);
    }

    // ── SetupTotp ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetupTotp_StoresSetupResponse()
    {
        StubDefaults();
        _handler.SetJsonResponse("api/auth/totp/setup", TotpSetup());

        var cut = Render<SettingsPage>();
        var method = typeof(SettingsPage).GetMethod("SetupTotp", InstPriv)!;

        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [])!);

        var setup = (TotpSetupResponse?)typeof(SettingsPage)
            .GetField("_totpSetup", InstPriv)!
            .GetValue(cut.Instance);

        Assert.NotNull(setup);
        Assert.Equal("JBSWY3DPEHPK3PXP", setup!.SharedKey);
    }

    // ── VerifyTotp ────────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyTotp_EmptyCode_LeavesTheFlagOff()
    {
        StubDefaults();
        var cut = Render<SettingsPage>();
        var method = typeof(SettingsPage).GetMethod("VerifyTotp", InstPriv)!;

        // _verifyCode is empty string by default.
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [])!);

        var enabled = (bool)typeof(SettingsPage)
            .GetField("_totpEnabled", InstPriv)!
            .GetValue(cut.Instance)!;
        Assert.False(enabled);
    }

    [Fact]
    public async Task VerifyTotp_WithCode_ApiReturnsTrue_SetsTotpEnabled()
    {
        StubDefaults();
        // Return HTTP 200 {} - VerifyTotpAsync checks the status code; Loose JSInterop returns default.
        _handler.SetJsonResponse("api/auth/totp/verify", true);

        var cut = Render<SettingsPage>();
        typeof(SettingsPage).GetField("_verifyCode", InstPriv)!.SetValue(cut.Instance, "123456");

        var method = typeof(SettingsPage).GetMethod("VerifyTotp", InstPriv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [])!);

        // A successful verify flips TOTP on.
        var enabled = (bool)typeof(SettingsPage)
            .GetField("_totpEnabled", InstPriv)!
            .GetValue(cut.Instance)!;
        Assert.True(enabled);
    }

    // ── DisableTotp ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DisableTotp_EmptyPassword_LeavesTheFlagOff()
    {
        StubDefaults();
        var cut = Render<SettingsPage>();
        var method = typeof(SettingsPage).GetMethod("DisableTotp", InstPriv)!;

        // _disablePassword is empty string by default.
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [])!);

        var enabled = (bool)typeof(SettingsPage)
            .GetField("_totpEnabled", InstPriv)!
            .GetValue(cut.Instance)!;
        Assert.False(enabled); // was already false - unchanged
    }

    [Fact]
    public async Task DisableTotp_WithPassword_ApiReturns_ResetsBusy()
    {
        StubDefaults();
        _handler.SetJsonResponse("api/auth/totp/disable", true);

        var cut = Render<SettingsPage>();
        typeof(SettingsPage).GetField("_disablePassword", InstPriv)!.SetValue(cut.Instance, "mypassword");

        var method = typeof(SettingsPage).GetMethod("DisableTotp", InstPriv)!;
        await cut.InvokeAsync(async () =>
            await (Task)method.Invoke(cut.Instance, [])!);

        var busy = (bool)typeof(SettingsPage)
            .GetField("_totpBusy", InstPriv)!
            .GetValue(cut.Instance)!;
        Assert.False(busy);
    }
}
