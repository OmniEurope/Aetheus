// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Auth;
using Aetheus.Front.Tests.Services;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Auth;

public class ChangePasswordRequiredTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ChangePasswordRequiredTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static void SetModel(IRenderedComponent<ChangePasswordRequired> cut, string current, string next)
    {
        var model = typeof(ChangePasswordRequired).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        var mt = model.GetType();
        mt.GetProperty("CurrentPassword")!.SetValue(model, current);
        mt.GetProperty("NewPassword")!.SetValue(model, next);
        mt.GetProperty("ConfirmPassword")!.SetValue(model, next);
    }

    private static Task InvokeSubmit(IRenderedComponent<ChangePasswordRequired> cut) =>
        cut.InvokeAsync(async () =>
            await (Task)typeof(ChangePasswordRequired).GetMethod("OnSubmit", Priv)!.Invoke(cut.Instance, [])!);

    private void UseCountingHubFactory() =>
        Services.AddSingleton<HubConnectionFactory>(services => new CountingHubConnectionFactory(
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<AuthStateProvider>()));

    [Fact]
    public void Renders_PasswordChangeForm()
    {
        var cut = Render<ChangePasswordRequired>();

        // The forced-change screen renders password inputs.
        Assert.NotEmpty(cut.FindAll("input"));
    }

    [Theory]
    [InlineData("CurrentPassword")]
    [InlineData("NewPassword")]
    [InlineData("ConfirmPassword")]
    public void EachPasswordField_HasASingleRevealToggle(string fieldId)
    {
        // Use only the package password reveal control.
        var cut = Render<ChangePasswordRequired>();
        var toggle = Assert.Single(cut.FindAll($"button[aria-controls='{fieldId}']"));
        Assert.Equal("password", cut.Find($"#{fieldId}").GetAttribute("type"));

        toggle.Click();

        Assert.Equal("text", cut.Find($"#{fieldId}").GetAttribute("type"));
    }

    [Fact]
    public async Task OnSubmit_Success_LogsOutAndRedirectsToLogin()
    {
        // 200 on the change endpoint ⇒ ApiStatus.Success ⇒ logout + redirect.
        _handler.SetResponse(HttpMethod.Post, "api/users/me/change-password", System.Net.HttpStatusCode.OK);
        UseCountingHubFactory();
        var auth = Services.GetRequiredService<AuthStateProvider>();
        var nav = Services.GetRequiredService<BunitNavigationManager>();
        var factory = (CountingHubConnectionFactory)Services.GetRequiredService<HubConnectionFactory>();

        var cut = Render<ChangePasswordRequired>();
        SetModel(cut, "OldPass1!", "NewPass2!");
        await InvokeSubmit(cut);

        // The change endpoint was hit…
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/users/me/change-password"));
        // …logout cleared the in-memory token…
        Assert.Null(auth.Token);
        Assert.Equal(1, factory.StopAllCount);
        // …and the user was redirected to the login screen.
        Assert.EndsWith("/login", nav.Uri);
    }

    /// <summary>Preserve the requested page through forced password change and subsequent
    /// sign-in.</summary>
    [Fact]
    public async Task OnSubmit_Success_KeepsTheRequestedPageForTheNewSignIn()
    {
        _handler.SetResponse(HttpMethod.Post, "api/users/me/change-password", System.Net.HttpStatusCode.OK);
        UseCountingHubFactory();
        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("http://localhost/account/change-password?returnUrl=%2Fprojects%2F1%2Fpipelines");

        var cut = Render<ChangePasswordRequired>();
        SetModel(cut, "OldPass1!", "NewPass2!");
        await InvokeSubmit(cut);

        Assert.Equal("http://localhost/login?returnUrl=%2Fprojects%2F1%2Fpipelines", nav.Uri);
    }

    [Fact]
    public async Task OnSubmit_Failure_SetsError_NoRedirect()
    {
        // 400 ⇒ ApiStatus failure ⇒ error shown, stay on the page.
        _handler.SetResponse(HttpMethod.Post, "api/users/me/change-password", System.Net.HttpStatusCode.BadRequest);
        var auth = Services.GetRequiredService<AuthStateProvider>();
        var nav = Services.GetRequiredService<BunitNavigationManager>();
        var startUri = nav.Uri;

        var cut = Render<ChangePasswordRequired>();
        SetModel(cut, "OldPass1!", "NewPass2!");
        await InvokeSubmit(cut);

        var error = (string?)typeof(ChangePasswordRequired).GetField("_error", Priv)!.GetValue(cut.Instance);
        Assert.False(string.IsNullOrEmpty(error));
        // Token still present (no logout) and no navigation away from the page.
        Assert.NotNull(auth.Token);
        Assert.Equal(startUri, nav.Uri);
    }

    [Fact]
    public void LogoutButton_LogsOutAndRedirectsToLogin()
    {
        UseCountingHubFactory();
        var auth = Services.GetRequiredService<AuthStateProvider>();
        var nav = Services.GetRequiredService<BunitNavigationManager>();
        var factory = (CountingHubConnectionFactory)Services.GetRequiredService<HubConnectionFactory>();

        var cut = Render<ChangePasswordRequired>();
        cut.FindMenuItem("Logout")!.Click();

        Assert.Null(auth.Token);
        Assert.Equal(1, factory.StopAllCount);
        Assert.EndsWith("/login", nav.Uri);
        // The server is told, so it forgets the held mailbox passwords.
        Assert.Contains(_handler.Requests, request =>
            request.Method == "POST" && request.Url.EndsWith("api/auth/logout", StringComparison.Ordinal));
    }
}
