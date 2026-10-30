// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using UsersPage = Aetheus.Front.Components.Users.Users;

namespace Aetheus.Front.Tests.Pages.Users;

public class UsersDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public UsersDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private static PaginatedResult<UserDto> MakePage(params UserDto[] users) =>
        new() { Items = users.ToList(), TotalCount = users.Length };

    private void SetupDefaults()
    {
        _handler.SetJsonResponse("api/users/roles", new List<string> { "Admin", "Contributor", "Reader" });
        _handler.SetJsonResponse("api/users", MakePage(
            new UserDto { Id = 1, Username = "alice", Email = "alice@example.com", IsActive = true, Roles = ["Admin"] },
            new UserDto { Id = 2, Username = "bob", Email = "bob@example.com", IsActive = false, Roles = ["Reader"] }
        ));
    }

    [Fact]
    public void Renders_UserList()
    {
        SetupDefaults();
        var cut = Render<UsersPage>();
        cut.WaitForState(() => cut.Markup.Contains("alice"));

        // The grid auto-loads the first page; both stubbed users are rendered.
        Assert.Contains("alice", cut.Markup);
        Assert.Contains("bob", cut.Markup);
    }

    [Fact]
    public async Task OnLoadData_SetsUsers()
    {
        SetupDefaults();
        var cut = Render<UsersPage>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var method = typeof(UsersPage).GetMethod("OnLoadData", Priv)!;
        var args = new GridLoadArgs { Skip = 0, Top = 20 };
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [args])!);

        var users = (List<UserDto>)typeof(UsersPage).GetField("_users", Priv)!.GetValue(cut.Instance)!;
        Assert.NotEmpty(users);
    }

    [Fact]
    public void OnCreate_OpensTheCreateUserPage()
    {
        SetupDefaults();
        var cut = Render<UsersPage>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // R-9: creating a user goes to its own page, no dialog opens.
        var dialog = Services.GetRequiredService<OmniDialogService>();
        Type? openedDialog = null;
        dialog.OnOpen += (_, type, _, _) => openedDialog = type;

        var method = typeof(UsersPage).GetMethod("OnCreate", Priv)!;
        method.Invoke(cut.Instance, []);

        Assert.Null(openedDialog);
        Assert.EndsWith("/admin/users/new", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Theory]
    [InlineData("Admin", OmniTone.Danger)]
    [InlineData("Contributor", OmniTone.Accent)]
    [InlineData("Reader", OmniTone.Accent)]
    [InlineData("Custom", OmniTone.Neutral)]
    public void GetRoleBadgeStyle_ReturnsExpected(string role, OmniTone expected)
    {
        var method = typeof(UsersPage).GetMethod("GetRoleBadgeStyle", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [role])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task OnRolesChanged_CallsApi()
    {
        SetupDefaults();
        _handler.SetJsonResponse("api/users/1", new UserDto { Id = 1, Username = "alice", Email = "alice@example.com", IsActive = true, Roles = ["Contributor"] });
        var cut = Render<UsersPage>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var user = new UserDto { Id = 1, Username = "alice", Email = "alice@example.com", IsActive = true, Roles = ["Admin"] };
        var method = typeof(UsersPage).GetMethod("OnRolesChanged", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [user, new List<string> { "Contributor" }])!);

        // Changing a user's roles persists via an update PUT to api/users/1.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/users/1"));
    }

    [Fact]
    public void LoadingState_StartsTrueFinishesFalse()
    {
        SetupDefaults();
        var cut = Render<UsersPage>();
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));
        var loading = (bool)typeof(UsersPage).GetField("_loading", Priv)!.GetValue(cut.Instance)!;
        Assert.False(loading);
    }
}
