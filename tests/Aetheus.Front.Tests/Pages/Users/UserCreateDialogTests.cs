// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Users;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages.Users;

/// <summary>
/// Behavioural tests for UserCreateDialog.razor.cs: the password-generate button mutates and
/// reveals the model password, the must-change-password checkbox binds, and a valid OnSubmit
/// issues the real POST and closes the dialog with the created DTO (captured by a spy OmniDialogService).
/// </summary>
public class UserCreateDialogTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public UserCreateDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
    }

    private void RegisterSpyDialog() =>
        Services.AddSingleton<OmniDialogService>(sp => new SpyDialogService(
            sp.GetRequiredService<NavigationManager>(),
            sp.GetRequiredService<IJSRuntime>()));

    private SpyDialogService Spy() => (SpyDialogService)Services.GetRequiredService<OmniDialogService>();

    private static object Model(IRenderedComponent<UserCreateDialog> cut) =>
        typeof(UserCreateDialog).GetField("_model", Priv)!.GetValue(cut.Instance)!;

    private static void SetModel(IRenderedComponent<UserCreateDialog> cut, string prop, object? value)
    {
        var model = Model(cut);
        model.GetType().GetProperty(prop)!.SetValue(model, value);
    }

    private static T GetModel<T>(IRenderedComponent<UserCreateDialog> cut, string prop) =>
        (T)Model(cut).GetType().GetProperty(prop)!.GetValue(Model(cut))!;

    // ── Password generation reveals a usable password ────────────────────────

    [Fact]
    public void PasswordField_HasOnlyTheDialogsRevealToggle()
    {
        // OmniPassword's own eye is off, the dialog's toggle (which Generate also uses) stays.
        var cut = Render<UserCreateDialog>(p => p.Add(c => c.AvailableRoles, ["Admin"]));

        Assert.Empty(cut.FindAll("button[aria-controls='Password']"));
        Assert.Single(cut.FindAll("button"), b => b.GetAttribute("aria-label") == "ShowPassword");
    }

    [Fact]
    public void GeneratePassword_PopulatesAndRevealsPassword()
    {
        var cut = Render<UserCreateDialog>(p => p.Add(c => c.AvailableRoles, ["Admin"]));

        // Initially the password field is masked (a password control, no plain value).
        Assert.True(string.IsNullOrEmpty(GetModel<string>(cut, "Password")));

        typeof(UserCreateDialog).GetMethod("GeneratePassword", Priv)!.Invoke(cut.Instance, []);

        // The model now carries a 16-char generated secret and the dialog flipped to "show".
        Assert.Equal(16, GetModel<string>(cut, "Password").Length);
        Assert.True((bool)typeof(UserCreateDialog).GetField("_showPassword", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public void GeneratePassword_SwitchesPasswordFieldToPlainText()
    {
        var cut = Render<UserCreateDialog>(p => p.Add(c => c.AvailableRoles, ["Admin"]));

        // Before generation the secret field is masked (a password-type input is present).
        Assert.Contains(cut.FindAll("input"), i => i.GetAttribute("type") == "password");

        cut.InvokeAsync(() =>
            typeof(UserCreateDialog).GetMethod("GeneratePassword", Priv)!.Invoke(cut.Instance, []));
        cut.Render();

        // After generation the dialog reveals the password - no masked input remains in the form.
        Assert.DoesNotContain(cut.FindAll("input"), i => i.GetAttribute("type") == "password");
    }

    // ── Must-change-password checkbox ────────────────────────────────────────

    [Fact]
    public void MustChangePassword_DefaultsTrue_AndRendersLabel()
    {
        var cut = Render<UserCreateDialog>(p => p.Add(c => c.AvailableRoles, ["Admin"]));

        // Default of the model is true (forced-change on first login).
        Assert.True(GetModel<bool>(cut, "MustChangePassword"));
        // The localized label (key returned verbatim by the stub) is rendered.
        Assert.Contains("MustChangePassword", cut.Markup);
    }

    // ── Valid submit issues the create call and closes with the DTO ──────────

    [Fact]
    public async Task OnSubmit_Valid_PostsCreateUser_AndClosesWithDto()
    {
        RegisterSpyDialog();
        _handler.SetJsonResponse(HttpMethod.Post, "api/users",
            new UserDto { Id = 7, Username = "jdoe", Roles = ["Admin"] });

        var cut = Render<UserCreateDialog>(p => p.Add(c => c.AvailableRoles, ["Admin"]));
        SetModel(cut, "Username", "jdoe");
        SetModel(cut, "Password", "Sup3rSecret!");

        await cut.InvokeAsync(async () =>
            await (Task)typeof(UserCreateDialog).GetMethod("OnSubmit", Priv)!.Invoke(cut.Instance, [])!);

        // A real POST to the users endpoint was sent…
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/users"));
        // …and the dialog closed handing back the created user.
        Assert.True(Spy().Closed);
        var dto = Assert.IsType<UserDto>(Spy().LastResult);
        Assert.Equal(7, dto.Id);
    }

    [Fact]
    public async Task OnSubmit_Conflict_ShowsApiReasonAndDoesNotClose()
    {
        RegisterSpyDialog();
        _handler.SetJsonResponse(
            HttpMethod.Post,
            "api/users",
            new ApiError { Message = "Username 'jdoe' is already taken." },
            System.Net.HttpStatusCode.Conflict);

        var cut = Render<UserCreateDialog>(p => p.Add(c => c.AvailableRoles, ["Admin"]));
        SetModel(cut, "Username", "jdoe");
        SetModel(cut, "Password", "Sup3rSecret!");

        await cut.InvokeAsync(async () =>
            await (Task)typeof(UserCreateDialog).GetMethod("OnSubmit", Priv)!.Invoke(cut.Instance, [])!);

        Assert.False(Spy().Closed);
        var error = (string?)typeof(UserCreateDialog).GetField("_error", Priv)!.GetValue(cut.Instance);
        Assert.Equal("Username 'jdoe' is already taken.", error);
    }
}
