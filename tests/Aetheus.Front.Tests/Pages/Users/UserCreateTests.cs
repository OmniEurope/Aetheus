// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Users;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Users;

/// <summary>
/// Behavioural tests for the user creation page (UserCreate.razor.cs). R-14 (owner decision 2026-10-28): no
/// password is typed here; the welcome mail carries the link. A valid submit issues the real POST without a
/// password; a mail that left is a success toast and opens the user, a mail that did not leave stays on the
/// page as a warning or an error, never as a success.
/// </summary>
public class UserCreateTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public UserCreateTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
        _handler.SetOrganizations();
    }

    private static object Model(IRenderedComponent<UserCreate> cut) =>
        typeof(UserCreate).GetField("_model", Priv)!.GetValue(cut.Instance)!;

    private static void SetModel(IRenderedComponent<UserCreate> cut, string prop, object? value)
    {
        var model = Model(cut);
        model.GetType().GetProperty(prop)!.SetValue(model, value);
    }

    private static Task SubmitAsync(IRenderedComponent<UserCreate> cut) =>
        cut.InvokeAsync(async () => await (Task)typeof(UserCreate).GetMethod("OnSubmit", Priv)!.Invoke(cut.Instance, [])!);

    private IRenderedComponent<UserCreate> RenderFilled()
    {
        var cut = Render<UserCreate>();
        SetModel(cut, "Username", "jdoe");
        SetModel(cut, "Email", "jdoe@example.org");
        return cut;
    }

    private void AnswerCreated(WelcomeMailResultDto? welcome) =>
        _handler.SetJsonResponse(HttpMethod.Post, "api/users",
            new UserDto { Id = 7, Username = "jdoe", Email = "jdoe@example.org", Roles = ["Admin"], WelcomeMail = welcome });

    [Fact]
    public void TheForm_HasNoPasswordField_AndSaysTheMailCarriesTheLink()
    {
        var cut = Render<UserCreate>();

        Assert.DoesNotContain(cut.FindAll("input"), input => input.GetAttribute("type") == "password");
        Assert.Empty(cut.FindAll("input#Password"));
        Assert.DoesNotContain("GeneratePassword", cut.Markup);
        Assert.DoesNotContain("MustChangePassword", cut.Markup);
        Assert.Contains("UserCreatePasswordByMail", cut.Markup);
    }

    [Fact]
    public async Task OnSubmit_WelcomeMailSent_PostsNoPassword_ToastsSuccess_AndOpensTheCreatedUser()
    {
        AnswerCreated(new WelcomeMailResultDto { Status = WelcomeMailStatus.Sent, To = "jdoe@example.org" });
        var cut = RenderFilled();

        await SubmitAsync(cut);

        var post = Assert.Single(_handler.RequestDetails, r => r.Method == "POST" && r.Url.Contains("api/users"));
        Assert.DoesNotContain("password", post.Body ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success && toast.Detail == "UserCreatedWelcomeSent");
        Assert.EndsWith("/users/7", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Theory]
    [InlineData(WelcomeMailStatus.Failed, "password-link-mail-failed", "omni-alert--danger")]
    [InlineData(WelcomeMailStatus.NotConfigured, "password-link-mail-notconfigured", "omni-alert--warning")]
    public async Task OnSubmit_WelcomeMailNotSent_StaysOnThePage_SaysWhy_AndToastsNoSuccess(
        WelcomeMailStatus status, string resultClass, string severityClass)
    {
        AnswerCreated(new WelcomeMailResultDto { Status = status, To = "jdoe@example.org", Message = "554 relay refused" });
        var cut = RenderFilled();
        var before = Services.GetRequiredService<NavigationManager>().Uri;

        await SubmitAsync(cut);
        cut.Render();

        Assert.Equal(before, Services.GetRequiredService<NavigationManager>().Uri);
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
        var alert = cut.Find("." + resultClass);
        Assert.Contains(severityClass, alert.ClassName);
        Assert.NotNull(cut.Find(".user-create-result"));
        Assert.Empty(cut.FindAll("form"));
    }

    [Fact]
    public async Task OnSubmit_AnAnswerWithoutTheMailOutcome_IsShownAsNotSent()
    {
        AnswerCreated(null);
        var cut = RenderFilled();

        await SubmitAsync(cut);
        cut.Render();

        Assert.NotNull(cut.Find(".password-link-mail-failed"));
        Assert.DoesNotContain(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
    }

    [Fact]
    public async Task OnSubmit_Conflict_ShowsApiReasonAndStaysOnThePage()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post,
            "api/users",
            new ApiError { Message = "Username 'jdoe' is already taken." },
            System.Net.HttpStatusCode.Conflict);

        var cut = RenderFilled();
        var before = Services.GetRequiredService<NavigationManager>().Uri;

        await SubmitAsync(cut);

        Assert.Equal(before, Services.GetRequiredService<NavigationManager>().Uri);
        var error = (string?)typeof(UserCreate).GetField("_error", Priv)!.GetValue(cut.Instance);
        Assert.Equal("Username 'jdoe' is already taken.", error);
    }
}
