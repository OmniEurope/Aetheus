// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using Aetheus.Front.Components.Auth;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>R-14: the page of the welcome mail's link reads the token from the address fragment, posts it with
/// the chosen password, and says a refused link plainly.</summary>
public class SetPasswordTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public SetPasswordTests() => _handler = BunitTestHelper.RegisterServices(this, authenticated: false);

    private IRenderedComponent<SetPassword> RenderAt(string address)
    {
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo(address);
        return Render<SetPassword>();
    }

    private static Task SubmitAsync(IRenderedComponent<SetPassword> cut, string password)
    {
        var model = (PasswordSetupModel)typeof(SetPassword).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        model.NewPassword = password;
        model.ConfirmPassword = password;
        var submit = typeof(SetPassword).GetMethod("OnSubmit", Priv)!;
        return cut.InvokeAsync(async () => await (Task)submit.Invoke(cut.Instance, [])!);
    }

    [Theory]
    [InlineData("https://a.example/login/set-password#token=abc_DEF-123", "abc_DEF-123")]
    [InlineData("https://a.example/login/set-password#other=1&token=xyz", "xyz")]
    [InlineData("https://a.example/login/set-password", null)]
    [InlineData("https://a.example/login/set-password#token=", null)]
    public void TokenOf_ReadsTheFragmentOnly(string uri, string? expected) =>
        Assert.Equal(expected, SetPassword.TokenOf(uri));

    [Fact]
    public void WithoutAToken_ThePageSaysTheLinkIsNotValid_AndOffersNoForm()
    {
        var cut = RenderAt("/login/set-password");

        Assert.NotNull(cut.Find(".password-setup-invalid"));
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public async Task Submitting_PostsTheTokenAndPassword_ThenGoesToSignIn()
    {
        _handler.SetResponse(HttpMethod.Post, "api/auth/password-setup", HttpStatusCode.NoContent);
        var cut = RenderAt("/login/set-password#token=the-token");

        await SubmitAsync(cut, "Chosen-Password-2026!");

        var body = _handler.RequestDetails.Single(r => r.Method == "POST" && r.Url.EndsWith("api/auth/password-setup", StringComparison.Ordinal)).Body!;
        var request = System.Text.Json.JsonSerializer.Deserialize<CompletePasswordSetupRequest>(body, JsonOptions.Web)!;
        Assert.Equal("the-token", request.Token);
        Assert.Equal("Chosen-Password-2026!", request.NewPassword);
        Assert.Equal("/login", new Uri(Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().Uri).AbsolutePath);
    }

    [Fact]
    public async Task ARefusedLink_IsSaidOnThePage_AndThePageStays()
    {
        _handler.SetResponse(HttpMethod.Post, "api/auth/password-setup", HttpStatusCode.BadRequest);
        var cut = RenderAt("/login/set-password#token=spent");

        await SubmitAsync(cut, "Chosen-Password-2026!");

        // Invoked outside an event handler, so the page is re-rendered by hand before it is read.
        cut.Render();
        Assert.NotNull(cut.Find(".password-setup-error"));
        Assert.Equal("/login/set-password", new Uri(Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().Uri).AbsolutePath);
    }
}
