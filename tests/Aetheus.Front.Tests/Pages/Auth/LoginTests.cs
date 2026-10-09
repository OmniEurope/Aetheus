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
        Assert.Contains("Username", cut.Markup);
        Assert.Contains("Password", cut.Markup);
    }

    /// <summary>
    /// Recette R2-123: the demo account sits in two read-only fields under the "Public demo" title,
    /// each with OE's Copy button welded to its end, outside the sign-in form.
    /// </summary>
    [Fact]
    public void PublicDemo_ShowsTheAccountInTwoReadOnlyCopyableFields()
    {
        _handler.SetJsonResponse("api/auth/public-demo", new PublicDemoInfoDto { Enabled = true });

        var cut = Render<Login>();

        var box = cut.WaitForElement(".public-demo-account");
        Assert.Contains("PublicDemo", box.QuerySelector("strong")!.TextContent, StringComparison.Ordinal);
        foreach (var (id, value) in new[] { ("public-demo-username", "admin"), ("public-demo-password", "aetheus-demo") })
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
    [InlineData("public-demo-username", "admin", true)]
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
        Assert.Contains("omni-button--secondary", fill.ClassList);
        fill.Click();

        Assert.Equal("admin", cut.Find("form input#Username").GetAttribute("value"));
        Assert.Equal("aetheus-demo", cut.Find("form input#Password").GetAttribute("value"));
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains(
            _handler.RequestDetails,
            request => request.Method == "POST" && request.Url.EndsWith("auth/login", StringComparison.Ordinal)));
        var body = _handler.RequestDetails.Last(request =>
            request.Method == "POST" && request.Url.EndsWith("auth/login", StringComparison.Ordinal)).Body;
        var sent = System.Text.Json.JsonSerializer.Deserialize<LoginRequest>(
            body!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("admin", sent!.Username);
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

        cut.Find("input#Username").Input("admin");
        cut.Find("input#Password").Input("aetheus-dev-admin-pwd");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(
            _handler.Requests,
            request => request.Method == "POST" && request.Url.EndsWith("auth/login", StringComparison.Ordinal)));
        var body = _handler.RequestDetails.Last(request =>
            request.Method == "POST" && request.Url.EndsWith("auth/login", StringComparison.Ordinal)).Body;
        var request = System.Text.Json.JsonSerializer.Deserialize<LoginRequest>(
            body!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("admin", request!.Username);
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

        cut.Find("input#Username").Input("admin");
        cut.Find("input#Password").Input("aetheus-dev-admin-pwd");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal("/", new Uri(nav.Uri).AbsolutePath), TimeSpan.FromSeconds(2));
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

        // Recette R-376: Login is blue and the only action button of the form (the password field's
        // own eye apart); the forgotten password is no longer a button beside it.
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

    /// <summary>
    /// Recette R-375: the form's action sits at the end of its row, like every card action. OE 1.4.0's
    /// sign-in shell has no actions slot (its footer is a block for the links), and the submit button
    /// must stay inside the form, so the row is OE's end-justified stack, with no local alignment rule.
    /// </summary>
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

        cut.Find("input#Username").Input("admin");
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

