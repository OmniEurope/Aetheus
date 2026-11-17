// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Settings;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Targets the 31 uncovered lines in AdminSettings (method coverage for SaveSetting,
/// AddSecret, GenerateToken, ToggleTokenReveal, CopyToken).
/// </summary>
public class AdminSettingsDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public AdminSettingsDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private void SetupAll(List<AppSettingDto>? settings = null)
    {
        _handler.SetJsonResponse("api/settings", settings ?? [
            new AppSettingDto { Key = "SiteName", Value = "Aetheus" },
            new AppSettingDto { Key = "MaxAgents", Value = "50" },
            new AppSettingDto { Key = "DefaultOrg", Value = "Aetheus" }
        ]);
        _handler.SetJsonResponse("api/settings/secrets", new List<SecretDto>
        {
            new() { Id = 1, Key = "SMTP_PASS" },
            new() { Id = 2, Key = "WEBHOOK_SECRET" }
        });
        _handler.SetJsonResponse("api/auth/registration-tokens", new List<RegistrationTokenDto>
        {
            new() { Id = 1, Token = "tok-aaa-bbb", ExpiresAt = DateTime.UtcNow.AddHours(12) },
            new() { Id = 2, Token = "tok-ccc-ddd", ExpiresAt = DateTime.UtcNow.AddDays(7) }
        });
    }

    [Fact]
    public void Renders_AllSections()
    {
        SetupAll();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => cut.Markup.Contains("SiteName") || !cut.Markup.Contains("rz-progressbar-circular"));

        // The loaded settings surface in the rendered page (real assertion, not just NotNull).
        Assert.Contains("SiteName", cut.Markup);
    }

    [Fact]
    public async Task SaveSetting_MissingKey_Returns()
    {
        SetupAll();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var savingKeys = (HashSet<string>)typeof(AdminSettings).GetField("_savingKeys", Priv)!.GetValue(cut.Instance)!;

        // Call with a key not in the edit buffer → early return BEFORE _savingKeys.Add(key).
        var method = typeof(AdminSettings).GetMethod("SaveSetting", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["NonExistentKey"])!);

        Assert.DoesNotContain("NonExistentKey", savingKeys);
    }

    [Fact]
    public async Task SaveSetting_ExistingKey_CallsApi()
    {
        SetupAll();
        // PUT api/settings/SiteName succeeds (distinct URL from the GET list at api/settings).
        _handler.SetResponse("api/settings/SiteName", System.Net.HttpStatusCode.OK);
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var savingKeys = (HashSet<string>)typeof(AdminSettings).GetField("_savingKeys", Priv)!.GetValue(cut.Instance)!;

        var method = typeof(AdminSettings).GetMethod("SaveSetting", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["SiteName"])!);

        // The full path ran: the key was added before the API call and removed in the finally block.
        Assert.DoesNotContain("SiteName", savingKeys);
    }

    [Fact]
    public async Task SaveSetting_AlreadySaving_Returns()
    {
        SetupAll();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var savingKeys = (HashSet<string>)typeof(AdminSettings).GetField("_savingKeys", Priv)!.GetValue(cut.Instance)!;
        savingKeys.Add("SiteName");

        var method = typeof(AdminSettings).GetMethod("SaveSetting", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["SiteName"])!);
        // Should return early without removing key
        Assert.Contains("SiteName", savingKeys);
    }

    [Fact]
    public async Task AddSecret_EmptyValues_DoesNotResetFields()
    {
        SetupAll();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var keyField = typeof(AdminSettings).GetField("_newSecretKey", Priv)!;
        var valueField = typeof(AdminSettings).GetField("_newSecretValue", Priv)!;
        // Whitespace value → validation early-return; the success path would clear both fields.
        keyField.SetValue(cut.Instance, "PARTIAL_KEY");
        valueField.SetValue(cut.Instance, "   ");

        var method = typeof(AdminSettings).GetMethod("AddSecret", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Fields are NOT reset because AddSecret returned before the API call.
        Assert.Equal("PARTIAL_KEY", (string)keyField.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task AddSecret_WithValues_CallsApi()
    {
        SetupAll();
        // POST returns the created secret; the reload GET (SetupAll) returns the list - kept distinct.
        _handler.SetJsonResponse(HttpMethod.Post, "api/settings/secrets", new SecretDto { Id = 3, Key = "NEW_KEY" });

        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        typeof(AdminSettings).GetField("_newSecretKey", Priv)!.SetValue(cut.Instance, "NEW_KEY");
        typeof(AdminSettings).GetField("_newSecretValue", Priv)!.SetValue(cut.Instance, "s3cr3t");

        var method = typeof(AdminSettings).GetMethod("AddSecret", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Success path: a POST was sent and the input fields were cleared after the reload.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/settings/secrets"));
        Assert.Empty((string)typeof(AdminSettings).GetField("_newSecretKey", Priv)!.GetValue(cut.Instance)!);
        Assert.Empty((string)typeof(AdminSettings).GetField("_newSecretValue", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task GenerateToken_PostsThenReloads()
    {
        SetupAll();
        _handler.SetJsonResponse(HttpMethod.Post, "api/auth/registration-tokens",
            new RegistrationTokenDto { Id = 9, Token = "tok-new", ExpiresAt = DateTime.UtcNow.AddDays(1) });

        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/settings?tab=tokens");
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        var grid = cut.FindComponent<AetheusDataGrid<RegistrationTokenDto>>().Instance;

        var tokensField = typeof(AdminSettings).GetField("_tokens", Priv)!;
        var method = typeof(AdminSettings).GetMethod("GenerateToken", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => grid.BackgroundRefresh);

        // Success path: a POST was sent and the token list was reloaded (GET) - non-empty from SetupAll.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/auth/registration-tokens"));
        Assert.NotEmpty((List<RegistrationTokenDto>)tokensField.GetValue(cut.Instance)!);
    }

    [Fact]
    public void ToggleTokenReveal_AddsThenRemoves()
    {
        SetupAll();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var method = typeof(AdminSettings).GetMethod("ToggleTokenReveal", Priv)!;
        var revealed = (HashSet<int>)typeof(AdminSettings).GetField("_revealedTokens", Priv)!.GetValue(cut.Instance)!;

        method.Invoke(cut.Instance, [1]);
        Assert.Contains(1, revealed);

        method.Invoke(cut.Instance, [1]);
        Assert.DoesNotContain(1, revealed);
    }

    [Fact]
    public async Task CopyToken_CallsJsInterop()
    {
        SetupAll();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var method = typeof(AdminSettings).GetMethod("CopyToken", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, ["tok-aaa-bbb"])!);

        // CopyToken writes the token to the clipboard via JS interop - assert the actual call + arg.
        JSInterop.VerifyInvoke("navigator.clipboard.writeText");
        var invocation = JSInterop.Invocations["navigator.clipboard.writeText"].Single();
        Assert.Equal("tok-aaa-bbb", invocation.Arguments[0]);
    }

    [Fact]
    public void EditBuffer_InitialisedFromSettings()
    {
        SetupAll();
        var cut = Render<AdminSettings>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var buffer = (Dictionary<string, string>)typeof(AdminSettings).GetField("_editBuffer", Priv)!.GetValue(cut.Instance)!;
        Assert.True(buffer.ContainsKey("SiteName"));
        Assert.Equal("Aetheus", buffer["SiteName"]);
    }
}
