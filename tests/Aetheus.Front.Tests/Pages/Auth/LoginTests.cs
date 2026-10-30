// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public class LoginTests : BunitContext
{
    public LoginTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: false);
    }

    private readonly BunitTestHelper.TestHandler _handler;

    [Fact]
    public void Renders_LoginForm()
    {
        _handler.SetJsonResponse("api/auth/public-demo", new PublicDemoInfoDto());
        var cut = Render<Login>();

        Assert.Contains("Login", cut.Markup);
        Assert.Contains("LoginEmail", cut.Markup);
        Assert.Contains("Password", cut.Markup);
    }

    /// <summary>PLAN-003 P3-2: people sign in by e-mail address, and the browser offers its saved
    /// address for it.</summary>
    [Fact]
    public void SignInField_IsAnEmailAddress_WithTheUsernameAutofillHint()
    {
        _handler.SetJsonResponse("api/auth/public-demo", new PublicDemoInfoDto());
        var cut = Render<Login>();

        var field = cut.Find("form input#Email");
        Assert.Equal("email", field.GetAttribute("type"));
        // One autofill field name only: "username email" is not a valid token list, so browsers read it as
        // no hint at all (autocomplete IDL ""), and password managers miss the sign-in field.
        Assert.Equal("username", field.GetAttribute("autocomplete"));
        // The label names the field and carries OE's required mark.
        Assert.Equal("LoginEmail*", cut.Find("label[for='Email']").TextContent.Trim());
        Assert.Empty(cut.FindAll("input#Username"));
    }

    /// <summary> The demo account sits in two read-only fields under the "Public demo" title, each
    /// with OE's Copy button welded to its end, outside the sign-in form. </summary>
    [Fact]
    public void PublicDemo_ShowsTheAccountInTwoReadOnlyCopyableFields()
    {
        _handler.SetJsonResponse("api/auth/public-demo", new PublicDemoInfoDto { Enabled = true });

        var cut = Render<Login>();

        var box = cut.WaitForElement(".public-demo-account");
        Assert.Contains("PublicDemo", box.QuerySelector("strong")!.TextContent, StringComparison.Ordinal);
        foreach (var (id, value) in new[] { ("public-demo-email", "admin@aetheus.local"), ("public-demo-password", "aetheus-demo") })
        {
            var input = box.QuerySelector($"input#{id}")!;
            Assert.True(input.HasAttribute("readonly"));
            Assert.Equal(value, input.GetAttribute("value"));
            Assert.NotNull(box.QuerySelector($"label[for='{id}']"));
            var copy = Assert.Single(input.Closest(".omni-text-box-field--copy")!.QuerySelectorAll("button"));
            Assert.Contains("omni-text-box-field__copy", copy.ClassList);
        }
        Assert.Null(box.Closest("form"));
    }

    [Theory]
    [InlineData("public-demo-email", "admin@aetheus.local", true)]
    [InlineData("public-demo-password", "aetheus-demo", false)]
    public void PublicDemo_CopyButton_PutsTheValueOnTheClipboard_AndOnlyARefusedCopyToasts(string id, string value, bool accepted)
    {
        var clipboard = JSInterop.SetupModule("./_content/OmniEurope.Blazor/omniInterop.js");
        clipboard.Setup<bool>("copyText", _ => true).SetResult(accepted);
        _handler.SetJsonResponse("api/auth/public-demo", new PublicDemoInfoDto { Enabled = true });
        var cut = Render<Login>();

        cut.WaitForElement($"input#{id}").Closest(".omni-text-box-field--copy")!.QuerySelector("button")!.Click();

        Assert.Equal(value, Assert.Single(clipboard.Invocations["copyText"]).Arguments[0]);
        cut.WaitForAssertion(() =>
        {
            if (accepted)
                Assert.DoesNotContain(Services.Toasts(), toast => toast.Summary == "CopyFailed");
            else
                Assert.Contains(Services.Toasts(), toast => toast.Summary == "CopyFailed");
        });
    }

    [Fact]
    public void PublicDemo_FillButton_WritesTheAccountIntoTheSignInForm_WhichSubmitsIt()
    {
        _handler.SetJsonResponse("api/auth/public-demo", new PublicDemoInfoDto { Enabled = true });
        _handler.SetJsonResponse("auth/login", new LoginResponse { Token = "jwt-token" });
        var cut = Render<Login>();

        var fill = cut.WaitForElement(".public-demo-account button.public-demo-fill");
        Assert.Contains("PublicDemoFillForm", fill.TextContent, StringComparison.Ordinal);
        Assert.Contains("omni-button--primary", fill.ClassList);
        fill.Click();

        Assert.Equal("admin@aetheus.local", cut.Find("form input#Email").GetAttribute("value"));
        Assert.Equal("aetheus-demo", cut.Find("form input#Password").GetAttribute("value"));
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains(
            _handler.RequestDetails,
            request => request.Method == "POST" && request.Url.EndsWith("auth/login", StringComparison.Ordinal)));
        var body = _handler.RequestDetails.Last(request =>
            request.Method == "POST" && request.Url.EndsWith("auth/login", StringComparison.Ordinal)).Body;
        var sent = System.Text.Json.JsonSerializer.Deserialize<LoginRequest>(
            body!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("admin@aetheus.local", sent!.Email);
        Assert.Equal("aetheus-demo", sent.Password);
    }

    [Fact]
    public void RegularEnvironment_HidesThePublicDemoAccount()
    {
        _handler.SetJsonResponse("api/auth/public-demo", new PublicDemoInfoDto());

        var cut = Render<Login>();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".public-demo-account"));
            Assert.DoesNotContain("aetheus-demo", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void InputEvents_AreSentByTheLoginForm()
    {
        _handler.SetJsonResponse("auth/login", new LoginResponse { Token = "jwt-token" });
        var cut = Render<Login>();

        cut.Find("input#Email").Input("admin@aetheus.local");
        cut.Find("input#Password").Input("aetheus-dev-admin-pwd");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(
            _handler.Requests,
            request => request.Method == "POST" && request.Url.EndsWith("auth/login", StringComparison.Ordinal)));
        var body = _handler.RequestDetails.Last(request =>
            request.Method == "POST" && request.Url.EndsWith("auth/login", StringComparison.Ordinal)).Body;
        var request = System.Text.Json.JsonSerializer.Deserialize<LoginRequest>(
            body!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("admin@aetheus.local", request!.Email);
        Assert.Equal("aetheus-dev-admin-pwd", request.Password);
    }

    [Fact]
    public void SuccessfulLogin_NavigatesToHome()
    {
        // FRONTTEST-005 (LoginTests): start on /login, which is not where a success lands, fill in
        // valid credentials, and wait for the token to be held and the page to move home.
        _handler.SetJsonResponse("auth/login", new LoginResponse { Token = "jwt-token" });
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        nav.NavigateTo("/login");
        var cut = Render<Login>();

        cut.Find("input#Email").Input("admin@aetheus.local");
        cut.Find("input#Password").Input("aetheus-dev-admin-pwd");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("/", new Uri(nav.Uri).AbsolutePath));
        Assert.Equal("jwt-token", Services.GetRequiredService<AuthStateProvider>().Token);
        Assert.Contains(nav.History, entry => new Uri(new Uri("http://localhost/"), entry.Uri).AbsolutePath == "/");
    }

    // FailedLogin_ShowsErrorMessage removed: it never submitted the form and asserted no error
    // message - only that the form rendered. The real invalid-credentials branch is covered by
    // OnSubmit_WithNullResult_ShowsError below (asserts _error == "InvalidCredentials").

    [Fact]
    public void Renders_LoginForm_HasSubmitButton()
    {
        var cut = Render<Login>();

        var buttons = cut.FindAll("button");
        Assert.NotEmpty(buttons);
    }

    [Fact]
    public void ForgotPassword_IsATextLinkInTheCardFooter_AndLoginIsTheFormsOnlyButton()
    {
        _handler.SetJsonResponse("api/auth/public-demo", new PublicDemoInfoDto());
        var cut = Render<Login>();

        // Login is the only form action button apart from password reveal; forgotten password is a
        // link.
        var actions = cut.Find("form").QuerySelectorAll("button")
            .Where(button => button.Closest(".omni-password") is null)
            .ToList();
        var login = Assert.Single(actions);
        Assert.Equal("submit", login.GetAttribute("type"));
        Assert.Contains("omni-button--primary", login.ClassList);

        var link = cut.Find(".omni-login-shell .omni-card__footer a.login-forgot-link");
        Assert.Equal("ForgotPassword", link.TextContent.Trim());
        Assert.Contains("forgot=true", link.GetAttribute("href"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("false", link.GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("#login-forgot-hint"));
    }

    /// <summary> The form's action sits at the end of its row, like every card action. OE 1.4.0's
    /// sign-in shell has no actions slot (its footer is a block for the links), and the submit
    /// button must stay inside the form, so the row is OE's end-justified stack, with no local
    /// alignment rule. </summary>
    [Fact]
    public void LoginAction_IsAtTheEndOfItsRow_InsideTheForm()
    {
        _handler.SetJsonResponse("api/auth/public-demo", new PublicDemoInfoDto());
        var cut = Render<Login>();

        var login = cut.Find("form button[type='submit']");
        var row = login.ParentElement!;
        Assert.Contains("omni-stack--horizontal", row.ClassList);
        Assert.Contains("omni-stack--justify-end", row.ClassList);
        var last = row.Children.Last();
        Assert.Equal("BUTTON", last.TagName);
        Assert.Equal("submit", last.GetAttribute("type"));
        Assert.Null(login.Closest(".omni-card__footer"));
    }

    [Fact]
    public void ForgotInTheAddress_ShowsTheHint_AndTheLinkClosesItKeepingTheReturnUrl()
    {
        _handler.SetJsonResponse("api/auth/public-demo", new PublicDemoInfoDto());
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/login?ReturnUrl=%2Fservers&forgot=true");

        var cut = Render<Login>();

        Assert.Contains("ForgotPasswordHint", cut.Find("#login-forgot-hint").TextContent, StringComparison.Ordinal);
        var link = cut.Find("a.login-forgot-link");
        Assert.Equal("true", link.GetAttribute("aria-expanded"));
        var href = link.GetAttribute("href")!;
        Assert.DoesNotContain("forgot=", href, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ReturnUrl=%2Fservers", href, StringComparison.Ordinal);
    }

    // R-14 (owner decision 2026-10-28): the forgotten-password hint is now a form that asks for a link.
    private IRenderedComponent<Login> RenderForgot()
    {
        _handler.SetJsonResponse("api/auth/public-demo", new PublicDemoInfoDto());
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/login?forgot=true");
        BunitTestHelper.Interactive(this);
        return Render<Login>();
    }

    [Fact]
    public void ForgotPassword_AskingForALink_PostsTheAddress_AndShowsTheSameNeutralAnswer()
    {
        _handler.SetResponse(HttpMethod.Post, "api/auth/password-reset", System.Net.HttpStatusCode.Accepted);
        var cut = RenderForgot();

        cut.Find("#login-forgot-hint input#ForgotEmail").Input("  jean@example.org ");
        cut.Find("#login-forgot-hint form").Submit();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#login-forgot-hint .login-forgot-sent")));
        var post = Assert.Single(_handler.RequestDetails, r => r.Method == "POST" && r.Url.Contains("api/auth/password-reset"));
        Assert.Contains("\"jean@example.org\"", post.Body, StringComparison.Ordinal);
        Assert.Contains("ForgotPasswordSent", cut.Find(".login-forgot-sent").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("#login-forgot-hint form"));
        // The main sign-in form is untouched.
        Assert.NotNull(cut.Find("form input#Email"));
    }

    [Fact]
    public void ForgotPassword_ARefusedRequest_SaysSo_AndKeepsTheForm()
    {
        _handler.SetResponse(HttpMethod.Post, "api/auth/password-reset", System.Net.HttpStatusCode.TooManyRequests);
        var cut = RenderForgot();

        cut.Find("#login-forgot-hint input#ForgotEmail").Input("jean@example.org");
        cut.Find("#login-forgot-hint form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("ForgotPasswordRefused", cut.Find(".login-forgot-error").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".login-forgot-sent"));
        Assert.NotNull(cut.Find("#login-forgot-hint form"));
    }

    [Fact]
    public void ForgotPassword_AnInvalidAddress_IsNotSent()
    {
        var cut = RenderForgot();

        cut.Find("#login-forgot-hint input#ForgotEmail").Input("not-an-address");
        cut.Find("#login-forgot-hint form").Submit();

        Assert.DoesNotContain(_handler.RequestDetails, r => r.Url.Contains("api/auth/password-reset"));
        Assert.Empty(cut.FindAll(".login-forgot-sent"));
    }

    [Fact]
    public void Renders_LoginForm_Unauthenticated()
    {
        var handler2 = BunitTestHelper.RegisterServices(this, authenticated: false);
        handler2.SetJsonResponse("auth/login", new LoginResponse { Token = "test" });

        var cut = Render<Login>();
        Assert.Contains("Login", cut.Markup);
    }

    [Fact]
    public void RedirectsToHome_WhenAlreadyAuthenticated()
    {
        var handler2 = BunitTestHelper.RegisterServices(this, authenticated: true);
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();

        var cut = Render<Login>();

        Assert.EndsWith("/", nav.Uri);
    }

    [Fact]
    public void Renders_RememberMeCheckbox()
    {
        // FRONTTEST-005 (LoginTests): the remember-me control itself, a checkbox carrying its label,
        // not merely "at least two inputs" (username and password alone satisfied that).
        var cut = Render<Login>();
        var checkbox = cut.Find("form input[type='checkbox']");
        Assert.Contains("RememberMe", checkbox.Closest("label")?.TextContent ?? string.Empty);
    }

    [Fact]
    public void Submit_WithRememberMeChecked_SendsRememberMeTrue()
    {
        _handler.SetJsonResponse("auth/login", new LoginResponse { Token = "jwt-token" });
        var cut = Render<Login>();

        cut.Find("input#Email").Input("admin@aetheus.local");
        cut.Find("input#Password").Input("secret");
        cut.Find("input.omni-checkbox").Change(true);
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(
            _handler.RequestDetails,
            request => request.Method == "POST" && request.Url.EndsWith("auth/login", StringComparison.Ordinal)));
        var body = _handler.RequestDetails.Last(request =>
            request.Method == "POST" && request.Url.EndsWith("auth/login", StringComparison.Ordinal)).Body;
        var request = System.Text.Json.JsonSerializer.Deserialize<LoginRequest>(
            body!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.True(request!.RememberMe);
    }

    [Fact]
    public async Task OnSubmit_WithNullResult_ShowsError()
    {
        // A JSON `null` login response makes LoginAsync return null → the invalid-credentials branch.
        _handler.SetJsonResponse<LoginResponse?>("auth/login", null);

        var cut = Render<Login>();
        var method = typeof(Login).GetMethod("OnSubmit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        // A null result surfaces the invalid-credentials error and leaves the loading flag cleared.
        var error = (string?)typeof(Login).GetField("_error", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.Equal("InvalidCredentials", error);
        Assert.False((bool)typeof(Login).GetField("_loading", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(cut.Instance)!);
    }
}

