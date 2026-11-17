// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using Aetheus.Front.Components.Auth;
using Aetheus.Front.Tests.Services;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Auth;

/// <summary>R-1 (owner decision 2026-10-29): the seeded administrator's account setup page.</summary>
public class AccountSetupTests : BunitContext
{
    private const string Endpoint = "api/users/me/account-setup";
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public AccountSetupTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        Services.AddSingleton<HubConnectionFactory>(services => new CountingHubConnectionFactory(
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<AuthStateProvider>()));
    }

    private static void Fill(IRenderedComponent<AccountSetup> cut)
    {
        cut.Find("input#SetupUsername").Input("owner");
        cut.Find("input#SetupEmail").Input("owner@example.com");
        cut.Find("input#CurrentPassword").Input("Installation-Pwd-2026");
        cut.Find("input#NewPassword").Input("Own-Administrator-Pwd-2026!");
        cut.Find("input#ConfirmPassword").Input("Own-Administrator-Pwd-2026!");
    }

    [Fact]
    public void Renders_TheNameTheAddressAndThePasswords()
    {
        var cut = Render<AccountSetup>();

        Assert.Equal("email", cut.Find("input#SetupEmail").GetAttribute("type"));
        foreach (var id in new[] { "SetupUsername", "CurrentPassword", "NewPassword", "ConfirmPassword" })
            Assert.Single(cut.FindAll($"input#{id}"));
        Assert.Contains("AccountSetupTitle", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Submit_SendsTheOwnAccount_ThenSignsOutTowardsTheNewSignIn_KeepingTheRequestedPage()
    {
        _handler.SetResponse(HttpMethod.Post, Endpoint, HttpStatusCode.NoContent);
        var auth = Services.GetRequiredService<AuthStateProvider>();
        var nav = Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("http://localhost/account/setup?returnUrl=%2Fservers");

        var cut = Render<AccountSetup>();
        Fill(cut);
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("http://localhost/login?returnUrl=%2Fservers", nav.Uri));
        var sent = Assert.Single(_handler.RequestDetails, request => request.Method == "POST" && request.Url.Contains(Endpoint, StringComparison.Ordinal));
        Assert.Contains("owner@example.com", sent.Body, StringComparison.Ordinal);
        Assert.Null(auth.Token);
        Assert.Equal(1, ((CountingHubConnectionFactory)Services.GetRequiredService<HubConnectionFactory>()).StopAllCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "AccountSetupConflict")]
    [InlineData(HttpStatusCode.BadRequest, "AccountSetupRefused")]
    public void Refusal_IsSaid_AndTheAccountStaysSignedInOnThePage(HttpStatusCode status, string message)
    {
        _handler.SetResponse(HttpMethod.Post, Endpoint, status);
        var auth = Services.GetRequiredService<AuthStateProvider>();
        var nav = Services.GetRequiredService<BunitNavigationManager>();
        var start = nav.Uri;

        var cut = Render<AccountSetup>();
        Fill(cut);
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(message, (string?)typeof(AccountSetup).GetField("_error", Priv)!.GetValue(cut.Instance)));
        Assert.NotNull(auth.Token);
        Assert.Equal(start, nav.Uri);
    }

    [Fact]
    public async Task Logout_SignsOutTowardsTheLogin()
    {
        var auth = Services.GetRequiredService<AuthStateProvider>();
        var nav = Services.GetRequiredService<BunitNavigationManager>();

        var cut = Render<AccountSetup>();
        await cut.FindMenuItem("Logout")!.ClickAsync(new());

        Assert.Null(auth.Token);
        Assert.EndsWith("/login", nav.Uri);
    }
}
