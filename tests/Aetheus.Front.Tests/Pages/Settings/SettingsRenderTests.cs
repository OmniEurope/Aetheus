// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Settings;
using Bunit;
using SettingsPage = Aetheus.Front.Components.Settings.Settings;

namespace Aetheus.Front.Tests.Pages;

public class SettingsRenderTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public SettingsRenderTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: false);
    }

    private void SetupStubs()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/users/me", new UserDto
        {
            Id = 1,
            Username = "testuser",
            IsActive = true,
            TotpEnabled = false,
            Roles = ["User"]
        });
    }

    [Fact]
    public void Renders_BasicMarkup()
    {
        SetupStubs();

        var cut = Render<SettingsPage>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        // OnInitializedAsync loads the current user (GetCurrentUserAsync → api/users/me) and
        // renders the localized page heading.
        Assert.Contains(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("api/users/me"));
        Assert.Contains("Settings", cut.Markup);
    }

    [Fact]
    public void Renders_WithRemovedProfileTab_FallsBackToTheFirstTab()
    {
        SetupStubs();

        var cut = Render<SettingsPage>(p => p.Add(x => x.Tab, "profile"));
        cut.WaitForState(() => cut.Markup.Length > 50);

        // The profile tab was removed (the display name cannot be changed), so an old "profile"
        // link lands on the first tab, appearance, instead of an empty panel.

        var index = (int)typeof(SettingsPage)
            .GetField("_selectedTabIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.Equal(0, index);
    }

    [Fact]
    public void Renders_WithAppearanceTab()
    {
        SetupStubs();

        var cut = Render<SettingsPage>(p => p.Add(x => x.Tab, "appearance"));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var index = (int)typeof(SettingsPage)
            .GetField("_selectedTabIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.Equal(0, index); // "appearance" is the first tab since the profile tab was removed
    }

    [Fact]
    public void Renders_WithSecurityTab()
    {
        SetupStubs();

        var cut = Render<SettingsPage>(p => p.Add(x => x.Tab, "security"));
        cut.WaitForState(() => cut.Markup.Length > 50);

        // "security" is index 2 in TabNames (appearance, notifications, security, tokens, about).
        var index = (int)typeof(SettingsPage)
            .GetField("_selectedTabIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.Equal(2, index);
    }

    [Fact]
    public void Renders_WithAboutTab()
    {
        SetupStubs();

        var cut = Render<SettingsPage>(p => p.Add(x => x.Tab, "about"));
        cut.WaitForState(() => cut.Markup.Length > 50);

        // "about" is index 4 (last) in TabNames, after the tokens tab.
        var index = (int)typeof(SettingsPage)
            .GetField("_selectedTabIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.Equal(4, index);
        // SITE-FUNC-TEMPLATE-001 guard: `v@identifier` is parsed as an e-mail address; `v@_version` is not
        // (an underscore after `@` starts code), and must keep rendering the version.
        Assert.Contains("vdev", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("v@_version", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationToggle_SavesThePreferenceOnTheServer()
    {
        SetupStubs();
        var failed = new NotificationPreferenceDto
        {
            EventType = NotificationEventTypes.PipelineFailed,
            IsEnabled = true,
            DefaultEnabled = true,
            CarriesProjectId = true
        };
        _handler.SetJsonResponse(HttpMethod.Get, "api/notifications/me/preferences", new List<NotificationPreferenceDto> { failed });
        _handler.SetJsonResponse(HttpMethod.Put, "api/notifications/me/preferences",
            new List<NotificationPreferenceDto> { failed with { IsEnabled = false, IsSaved = true } });

        var cut = Render<SettingsPage>(p => p.Add(x => x.Tab, "notifications"));
        // Each event is a settings tile whose switch carries the preference text.
        cut.WaitForElement(".notification-preference-toggle button[role='switch']").Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request =>
            request.Method == "PUT"
            && request.Url.Contains("api/notifications/me/preferences", StringComparison.Ordinal)
            && request.Body!.Contains("\"eventType\":\"pipeline.failed\"", StringComparison.Ordinal)
            && request.Body.Contains("\"isEnabled\":false", StringComparison.Ordinal)));
        Assert.DoesNotContain(JSInterop.Invocations, invocation =>
            invocation.Identifier == "localStorage.setItem"
            && ((string?)invocation.Arguments[0])?.StartsWith("aetheus_notif_", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void OnTabChange_UpdatesSelectedIndex()
    {
        SetupStubs();

        var cut = Render<SettingsPage>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        var method = typeof(SettingsPage).GetMethod("OnTabChange", BindingFlags.NonPublic | BindingFlags.Instance)!;
        cut.InvokeAsync(() => method.Invoke(cut.Instance, ["security"]));

        var index = (int)typeof(SettingsPage)
            .GetField("_selectedTabIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.Equal(2, index);
    }

    [Fact]
    public void Renders_WithTotpEnabled()
    {
        // GetCurrentUserAsync fetches api/users/me (not api/auth/me)
        _handler.SetJsonResponse("api/users/me", new UserDto
        {
            Id = 1,
            Username = "testuser",
            IsActive = true,
            TotpEnabled = true,
            Roles = ["User"]
        });

        var cut = Render<SettingsPage>(p => p.Add(x => x.Tab, "security"));
        cut.WaitForState(() => cut.Markup.Length > 50);

        var totpEnabled = (bool)typeof(SettingsPage)
            .GetField("_totpEnabled", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        Assert.True(totpEnabled);
    }

    [Fact]
    public async Task SetupTotp_PopulatesSetupResponse()
    {
        SetupStubs();
        _handler.SetJsonResponse("api/auth/totp/setup", new TotpSetupResponse
        {
            SharedKey = "JBSWY3DPEHPK3PXP",
            AuthenticatorUri = "otpauth://totp/test",
            RecoveryCodes = ["code1", "code2"]
        });

        var cut = Render<SettingsPage>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        var method = typeof(SettingsPage).GetMethod("SetupTotp", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        var setup = (TotpSetupResponse?)typeof(SettingsPage)
            .GetField("_totpSetup", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance);
        Assert.NotNull(setup);
        Assert.Equal("JBSWY3DPEHPK3PXP", setup!.SharedKey);
    }
}
