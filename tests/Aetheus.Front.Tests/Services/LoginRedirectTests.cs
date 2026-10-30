// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Services;

/// <summary>Preserve only a validated local return page through sign-in.</summary>
public sealed class LoginRedirectTests : BunitContext
{
    private const string RunLogs = "/pipelines/runs/2323?tab=logs";
    private const string EncodedRunLogs = "%2Fpipelines%2Fruns%2F2323%3Ftab%3Dlogs";

    private readonly BunitTestHelper.TestHandler _handler;

    public LoginRedirectTests() => _handler = BunitTestHelper.RegisterServices(this, authenticated: false);

    private BunitNavigationManager Nav => Services.GetRequiredService<BunitNavigationManager>();

    [Fact]
    public void ToLogin_CarriesTheCurrentPathQueryAndFragment()
    {
        Nav.NavigateTo("http://localhost/pipelines/runs/2323?tab=logs#step-4");

        Assert.Equal("/login?returnUrl=%2Fpipelines%2Fruns%2F2323%3Ftab%3Dlogs%23step-4", LoginRedirect.ToLogin(Nav));
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost/login")]
    [InlineData("http://localhost/account/change-password")]
    public void ToLogin_FromTheDashboardOrAnAuthPage_CarriesNothing(string uri)
    {
        Nav.NavigateTo(uri);

        Assert.Equal("/login", LoginRedirect.ToLogin(Nav));
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("/https://evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("pipelines/runs/1")]
    [InlineData("/pipe\nlines")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("/login")]
    public void Resolve_RefusesAnythingButAPathOfThisApplication(string? returnUrl)
    {
        Assert.Equal("/", LoginRedirect.Resolve(returnUrl));
        Assert.Equal("/login", LoginRedirect.WithReturnUrl(LoginRedirect.LoginPath, returnUrl));
    }

    [Theory]
    [InlineData(RunLogs)]
    [InlineData("/projects/1/pipelines")]
    [InlineData("/redirect?to=https://example.org")]
    public void Resolve_KeepsAPathOfThisApplication(string returnUrl) =>
        Assert.Equal(returnUrl, LoginRedirect.Resolve(returnUrl));

    /// <summary>The whole trip: a pasted URL without a session, the login page, then back to it.</summary>
    [Fact]
    public void Login_GoesBackToTheRequestedPage()
    {
        _handler.SetJsonResponse("auth/login", new LoginResponse { Token = "jwt-token" });
        Nav.NavigateTo($"http://localhost/login?returnUrl={EncodedRunLogs}");

        var cut = Render<Login>();
        cut.Find("input#Email").Input("admin@aetheus.local");
        cut.Find("input#Password").Input("secret-password");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("http://localhost" + RunLogs, Nav.Uri));
    }

    [Theory]
    [InlineData("https%3A%2F%2Fevil.example")]
    [InlineData("%2F%2Fevil.example")]
    public void Login_WithAForeignReturnUrl_LandsOnTheDashboard(string encodedReturnUrl)
    {
        _handler.SetJsonResponse("auth/login", new LoginResponse { Token = "jwt-token" });
        Nav.NavigateTo($"http://localhost/login?returnUrl={encodedReturnUrl}");

        var cut = Render<Login>();
        cut.Find("input#Email").Input("admin@aetheus.local");
        cut.Find("input#Password").Input("secret-password");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("http://localhost/", Nav.Uri));
    }

    /// <summary>Recette R-24: an approval mail's Approve link opened while signed out leads through the sign-in
    /// to the approval page without its action, so signing in decides nothing.</summary>
    [Fact]
    public void R24_ToLogin_DropsTheOneShotAction_KeepingTheRestOfTheAddress()
    {
        Nav.NavigateTo("http://localhost/pipelines/runs/12/approvals/34?action=approve&tab=logs#top");

        Assert.Equal("/login?returnUrl=" + Uri.EscapeDataString("/pipelines/runs/12/approvals/34?tab=logs#top"), LoginRedirect.ToLogin(Nav));
    }

    [Theory]
    [InlineData("/pipelines/runs/12/approvals/34?action=approve", "/pipelines/runs/12/approvals/34")]
    [InlineData("/pipelines/runs/12/approvals/34?ACTION=reject&x=1", "/pipelines/runs/12/approvals/34?x=1")]
    [InlineData("/pipelines/runs/12/approvals/34?x=1&action=approve&action=reject", "/pipelines/runs/12/approvals/34?x=1")]
    [InlineData("/pipelines/runs/12?tab=logs", "/pipelines/runs/12?tab=logs")]
    public void R24_Resolve_NeverReturnsWithAnAction(string returnUrl, string expected) =>
        Assert.Equal(expected, LoginRedirect.Resolve(returnUrl));

    /// <summary>The whole trip, crafted or not: signing in from a return address that carries an action lands on
    /// the page without it.</summary>
    [Fact]
    public void R24_Login_FromAReturnUrlWithAnAction_LandsWithoutIt()
    {
        _handler.SetJsonResponse("auth/login", new LoginResponse { Token = "jwt-token" });
        Nav.NavigateTo("http://localhost/login?returnUrl=" + Uri.EscapeDataString("/pipelines/runs/12/approvals/34?action=approve"));

        var cut = Render<Login>();
        cut.Find("input#Email").Input("admin@aetheus.local");
        cut.Find("input#Password").Input("secret-password");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("http://localhost/pipelines/runs/12/approvals/34", Nav.Uri));
    }

    /// <summary>A forced password change sits between the login and the page: the page is kept through it.</summary>
    [Fact]
    public void Login_ThroughTheForcedPasswordChange_KeepsTheRequestedPage()
    {
        _handler.SetJsonResponse("auth/login", new LoginResponse { Token = "jwt-token", MustChangePassword = true });
        Nav.NavigateTo($"http://localhost/login?returnUrl={EncodedRunLogs}");

        var cut = Render<Login>();
        cut.Find("input#Email").Input("admin@aetheus.local");
        cut.Find("input#Password").Input("secret-password");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(
            $"http://localhost/account/change-password?returnUrl={EncodedRunLogs}", Nav.Uri));
    }
}
