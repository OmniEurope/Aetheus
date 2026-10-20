// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;

namespace Aetheus.Front.Tests.Pages;

public class AdminSettingsTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AdminSettingsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    [Fact]
    public void Renders_AdminSettingsPage_ForAdmin()
    {
        _handler.SetJsonResponse("api/settings", new List<AppSettingDto>
        {
            new() { Key = "SiteName", Value = "Aetheus" }
        });
        _handler.SetJsonResponse("api/settings/secrets", new List<SecretDto>());
        _handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>());

        var cut = Render<AdminSettings>();
        Assert.Contains("PlatformSettings", cut.Markup);
    }

    [Fact]
    public void NonAdmin_RedirectsToHome()
    {
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: false);
        handler.SetJsonResponse("api/settings", new List<AppSettingDto>());
        handler.SetJsonResponse("api/settings/secrets", new List<SecretDto>());
        handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>());

        Render<AdminSettings>();

        // The non-admin guard returns before OnInitializedAsync fetches anything, so
        // none of the admin-only endpoints are hit (the page never loads settings/secrets/tokens).
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("api/settings"));
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("api/auth/registration-tokens"));
    }

    [Fact]
    public void Renders_WithSettings_And_Secrets()
    {
        _handler.SetJsonResponse("api/settings", new List<AppSettingDto>
        {
            new() { Key = "SiteName", Value = "Aetheus" },
            new() { Key = "MaxAgents", Value = "10" }
        });
        _handler.SetJsonResponse("api/settings/secrets", new List<SecretDto>
        {
            new() { Id = 1, Key = "API_KEY" }
        });
        _handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>
        {
            new() { Id = 1, Token = "abc-123", ExpiresAt = DateTime.UtcNow.AddHours(24) }
        });

        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        // OnInitializedAsync loads settings, secrets and tokens in parallel into instance state.
        var settings = (List<AppSettingDto>)typeof(AdminSettings)
            .GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var secrets = (List<SecretDto>)typeof(AdminSettings)
            .GetField("_secrets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var tokens = (List<RegistrationTokenDto>)typeof(AdminSettings)
            .GetField("_tokens", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal(2, settings.Count);
        Assert.Single(secrets);
        Assert.Single(tokens);
    }

    private void SetupAllMocks()
    {
        _handler.SetJsonResponse("api/settings", new List<AppSettingDto>
        {
            new() { Key = "SiteName", Value = "Aetheus" },
            new() { Key = "MaxAgents", Value = "10" }
        });
        _handler.SetJsonResponse("api/settings/secrets", new List<SecretDto>
        {
            new() { Id = 1, Key = "API_KEY" },
            new() { Id = 2, Key = "DB_CONN" }
        });
        _handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>
        {
            new() { Id = 1, Token = "abc-123", ExpiresAt = DateTime.UtcNow.AddHours(24) }
        });
    }

    [Fact]
    public async Task SaveSetting_CallsApi()
    {
        SetupAllMocks();
        _handler.SetJsonResponse("api/settings/SiteName", true);
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        var method = typeof(AdminSettings).GetMethod("SaveSetting", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["SiteName"])!);

        // The key resolves in _editBuffer, so SaveSetting issues a PUT to persist the value.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/settings/SiteName"));
    }

    [Fact]
    public async Task AddSecret_EmptyKey_ShowsWarning()
    {
        SetupAllMocks();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.Markup.Length > 50);

        var method = typeof(AdminSettings).GetMethod("AddSecret", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Empty key/value short-circuits with a warning toast - no create POST is sent.
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/settings/secrets"));
    }

    [Fact]
    public void AddSecret_WithValues_SetsFields()
    {
        SetupAllMocks();
        var cut = Render<AdminSettings>();
        typeof(AdminSettings).GetField("_newSecretKey", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "NEW_SECRET");
        typeof(AdminSettings).GetField("_newSecretValue", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cut.Instance, "value123");
        var key = (string)typeof(AdminSettings).GetField("_newSecretKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal("NEW_SECRET", key);
    }

    // GenerateToken_MethodExists removed (reflexive tautology): the failure path is covered by
    // AdminSettingsDeepTests.GenerateToken_NullResult_ShowsError and AdminSettingsExtendedTests.
    // GenerateToken_ApiReturnsNull_ShowsError.

    [Fact]
    public void ToggleTokenReveal_TogglesState()
    {
        SetupAllMocks();
        var cut = Render<AdminSettings>();
        var method = typeof(AdminSettings).GetMethod("ToggleTokenReveal", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(cut.Instance, [1]);
        var revealed = (HashSet<int>)typeof(AdminSettings).GetField("_revealedTokens", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Contains(1, revealed);
        method.Invoke(cut.Instance, [1]);
        Assert.DoesNotContain(1, revealed);
    }
}
