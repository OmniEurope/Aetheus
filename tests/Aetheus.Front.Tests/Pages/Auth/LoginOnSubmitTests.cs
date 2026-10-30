// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Auth;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>Covers Login.OnSubmit - the login flow (success, TOTP-required, invalid, network error).</summary>
public class LoginOnSubmitTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public LoginOnSubmitTests()
    {
        // Unauthenticated so OnInitialized doesn't redirect away.
        _handler = BunitTestHelper.RegisterServices(this, authenticated: false);
    }

    private static void SetCredentials(object instance, string user, string pass)
    {
        var model = typeof(Login).GetField("_model", Priv)!.GetValue(instance)!;
        var mt = model.GetType();
        mt.GetProperty("Email")!.SetValue(model, user);
        mt.GetProperty("Password")!.SetValue(model, pass);
    }

    private static Task InvokeSubmit(IRenderedComponent<Login> cut)
    {
        var m = typeof(Login).GetMethod("OnSubmit", Priv)!;
        return cut.InvokeAsync(async () => await (Task)m.Invoke(cut.Instance, [])!);
    }

    [Fact]
    public async Task OnSubmit_ValidCredentials_LogsInAndNavigates()
    {
        _handler.SetJsonResponse("api/auth/login", new LoginResponse { Token = MakeJwt(), RefreshToken = "r" });
        _handler.SetJsonResponse("api/users/me/permissions", new UserPermissionSummaryDto { EffectivePermissions = [] });

        // FRONTTEST-005: the outcome is read from what a login does, not from an error field that was
        // already null: the credentials are posted, the session holds the issued token, and the page
        // leaves /login for home.
        var token = MakeJwt();
        _handler.SetJsonResponse(HttpMethod.Post, "api/auth/login", new LoginResponse { Token = token, RefreshToken = "r" });
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/login");
        var cut = Render<Login>();
        SetCredentials(cut.Instance, "admin@aetheus.local", "password123");
        await InvokeSubmit(cut);

        var request = System.Text.Json.JsonSerializer.Deserialize<LoginRequest>(
            _handler.RequestDetails.Single(r => r.Method == "POST" && r.Url.EndsWith("api/auth/login", StringComparison.Ordinal)).Body!,
            JsonOptions.Web)!;
        Assert.Equal("admin@aetheus.local", request.Email);
        Assert.Equal("password123", request.Password);
        var auth = Services.GetRequiredService<AuthStateProvider>();
        Assert.True(auth.IsAuthenticated);
        Assert.Equal(token, auth.Token);
        Assert.Equal("/", new Uri(nav.Uri).AbsolutePath);
    }

    [Fact]
    public async Task OnSubmit_ValidCredentials_WithAReturnUrl_NavigatesThere()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/auth/login", new LoginResponse { Token = MakeJwt(), RefreshToken = "r" });
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/login?returnUrl=%2Fservers%2F7");
        var cut = Render<Login>();
        SetCredentials(cut.Instance, "admin@aetheus.local", "password123");
        await InvokeSubmit(cut);

        Assert.Equal("/servers/7", new Uri(nav.Uri).AbsolutePath);
    }

    [Fact]
    public async Task OnSubmit_TotpRequired_ShowsTotpInput()
    {
        _handler.SetJsonResponse("api/auth/login", new LoginResponse { Token = null!, TotpRequired = true });

        var cut = Render<Login>();
        SetCredentials(cut.Instance, "admin@aetheus.local", "password123");
        await InvokeSubmit(cut);

        var totp = (bool)typeof(Login).GetField("_totpRequired", Priv)!.GetValue(cut.Instance)!;
        Assert.True(totp);
    }

    [Fact]
    public async Task TotpRequired_RecoveryMode_SubmitsRecoveryCodeInsteadOfTotpCode()
    {
        _handler.SetJsonResponse("api/auth/login", new LoginResponse { Token = null!, TotpRequired = true });
        var cut = Render<Login>();
        SetCredentials(cut.Instance, "admin@aetheus.local", "password123");
        await InvokeSubmit(cut);

        cut.Render();
        cut.Find(".totp-mode-toggle button").Click();
        cut.Find("input#oe-pages-auth-login-3").Input("ABCDE-12345");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            var body = _handler.RequestDetails.Last(request =>
                request.Method == "POST" && request.Url.EndsWith("api/auth/login", StringComparison.Ordinal)).Body;
            var login = System.Text.Json.JsonSerializer.Deserialize<LoginRequest>(
                body!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Assert.Equal("ABCDE-12345", login!.RecoveryCode);
            Assert.Null(login.TotpCode);
        });
    }

    [Fact]
    public async Task OnSubmit_NullResult_SetsInvalidCredentialsError()
    {
        _handler.SetResponse("api/auth/login", System.Net.HttpStatusCode.Unauthorized);

        var cut = Render<Login>();
        SetCredentials(cut.Instance, "admin@aetheus.local", "wrong");
        await InvokeSubmit(cut);

        var error = (string?)typeof(Login).GetField("_error", Priv)!.GetValue(cut.Instance);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task OnSubmit_RateLimited_ShowsSpecificInlineError()
    {
        _handler.SetResponse("api/auth/login", System.Net.HttpStatusCode.TooManyRequests);
        var cut = Render<Login>();
        SetCredentials(cut.Instance, "admin@aetheus.local", "password123");

        await InvokeSubmit(cut);

        var error = (string?)typeof(Login).GetField("_error", Priv)!.GetValue(cut.Instance);
        Assert.Equal("LoginRateLimited", error);
    }

    [Fact]
    public async Task OnSubmit_ClearsLoadingFlagAfterCompletion()
    {
        _handler.SetJsonResponse("api/auth/login", new LoginResponse { Token = MakeJwt() });
        _handler.SetJsonResponse("api/users/me/permissions", new UserPermissionSummaryDto { EffectivePermissions = [] });

        var cut = Render<Login>();
        SetCredentials(cut.Instance, "admin@aetheus.local", "password123");
        await InvokeSubmit(cut);

        var loading = (bool)typeof(Login).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
    }

    [Fact]
    public void PasswordField_HasASingleRevealToggle_ThatRevealsTheField()
    {
        // Use only the package password reveal control.
        var cut = Render<Login>();
        var toggles = cut.FindAll("button[aria-controls='Password']");
        Assert.Single(toggles);
        var revealName = toggles[0].GetAttribute("aria-label");
        Assert.Single(cut.FindAll("button"), b => b.GetAttribute("aria-label") == revealName);
        Assert.Equal("password", cut.Find("#Password").GetAttribute("type"));

        toggles[0].Click();

        Assert.Equal("text", cut.Find("#Password").GetAttribute("type"));
    }

    private static string MakeJwt()
    {
        var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}"));
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { sub = "admin", exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });
        var p64 = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{header}.{p64}.sig";
    }
}
