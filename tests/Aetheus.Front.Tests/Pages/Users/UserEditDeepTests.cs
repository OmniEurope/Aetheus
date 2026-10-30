// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Users;
using Aetheus.Front.Tests.Architecture;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Users;

public class UserEditDeepTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public UserEditDeepTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetOrganizations();
    }

    private void SetupRoles(params string[] roles) =>
        _handler.SetPaginatedJsonResponse("api/roles",
            roles.Select((role, index) => new RoleDto
            {
                Id = index + 1,
                Name = role,
                Description = $"{role} description"
            }));

    private void SetupExistingUser(int id = 5)
    {
        SetupRoles("Admin", "Contributor", "Reader");
        _handler.SetJsonResponse($"api/users/{id}", new UserDto
        {
            Id = id,
            Username = "tester",
            Email = "tester@example.com",
            IsActive = true,
            Roles = ["Contributor"]
        });
        _handler.SetJsonResponse($"api/users/{id}/effective-permissions", new UserPermissionSummaryDto
        {
            EffectivePermissions = [
                new EffectivePermissionDto { ResourceType = ResourceType.Server, Permission = Permission.Read }
            ]
        });
    }

    [Fact]
    public void Renders_EditForm_ForExistingUser()
    {
        SetupExistingUser();
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        // The existing user is fetched (GET api/users/5) and mapped into the edit model.
        Assert.Contains(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("api/users/5"));
        var model = typeof(UserEdit).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        var username = (string)model.GetType().GetProperty("Username")!.GetValue(model)!;
        Assert.Equal("tester", username);
    }

    [Fact]
    public async Task OnSubmit_ExistingUser_UpdatesUser()
    {
        SetupExistingUser();
        _handler.SetJsonResponse("api/users/5", new UserDto { Id = 5, Username = "tester", Email = "tester@example.com", IsActive = true, Roles = ["Contributor"] });
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var method = typeof(UserEdit).GetMethod("OnSubmit", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [])!);

        // Existing-user path issues an update PUT to api/users/5.
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/users/5"));
    }

    [Fact]
    public async Task OnSubmit_CurrentUser_RefreshesLivePermissionService()
    {
        SetupExistingUser();
        _handler.SetJsonResponse("api/users/5", new UserDto
        {
            Id = 5,
            Username = "tester",
            IsActive = true,
            Roles = ["Contributor"]
        });
        _handler.SetJsonResponse(
            HttpMethod.Get,
            "api/users/5/effective-permissions",
            new UserPermissionSummaryDto
            {
                EffectivePermissions =
                [
                    new EffectivePermissionDto
                    {
                        ResourceType = ResourceType.Server,
                        Permission = Permission.Read
                    }
                ]
            });
        var auth = Services.GetRequiredService<AuthStateProvider>();
        await auth.LoginAsync(CreateJwt("tester"));
        Assert.Equal("tester", auth.Username);
        var permissions = Services.GetRequiredService<PermissionService>();
        permissions.SetPermissions([], false);
        var roundTrip = await Services.GetRequiredService<ApiClient>()
            .Auth.GetUserEffectivePermissionsAsync(5, Xunit.TestContext.Current.CancellationToken);
        Assert.NotEmpty(roundTrip!.EffectivePermissions);

        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => cut.Instance.EditedUsername is not null);
        Assert.Equal("tester", cut.Instance.EditedUsername);
        Assert.True(cut.Instance.IsEditingCurrentUser);
        Assert.Equal("tester", auth.Username);
        _handler.Requests.Clear();

        await cut.InvokeAsync(cut.Instance.OnSubmit);

        Assert.Contains(
            _handler.Requests,
            request => request.Method == "GET"
                && request.Url.Contains("api/users/5/effective-permissions", StringComparison.Ordinal));
        Assert.Contains(
            permissions.GetPermissions(),
            permission => permission.ResourceType == ResourceType.Server
                && permission.Permission == Permission.Read);
        Assert.True(permissions.CanReadAny(ResourceType.Server));
    }

    private static string CreateJwt(string username)
    {
        var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub = username,
            role = "Admin",
            unique_name = username,
            exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        }))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{header}.{payload}.sig";
    }

    [Fact]
    public async Task OnChangePassword_EmptyPassword_Returns()
    {
        SetupExistingUser();
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        SetFormValue(cut.Instance, "_passwordChange", "NewPassword", "");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "OnChangePassword", "_passwordChange"));

        // An empty password short-circuits before the change-password call is made.
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("change-password"));
    }

    [Fact]
    public async Task OnChangePassword_ValidPassword_CallsApi()
    {
        SetupExistingUser();
        _handler.SetResponse("api/users/5/change-password", System.Net.HttpStatusCode.OK);
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        SetFormValue(cut.Instance, "_passwordChange", "NewPassword", "newpassword1");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "OnChangePassword", "_passwordChange"));

        // A valid password triggers the change-password POST to api/users/5/change-password.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/users/5/change-password"));
    }

    [Theory]
    [InlineData(Permission.Admin, OmniTone.Danger)]
    [InlineData(Permission.Write, OmniTone.Warning)]
    [InlineData(Permission.Read, OmniTone.Accent)]
    public void GetPermissionBadgeStyle_ReturnsExpected(Permission permission, OmniTone expected)
    {
        var method = typeof(UserEdit).GetMethod("GetPermissionBadgeStyle", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [permission])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void R152_TheFlagSwitches_StayEnabledSoTheyKeepFocus_AndTheValidOutlineSkipsThem()
    {
        SetupExistingUser();
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 5));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"));

        var switches = cut.FindAll("button[role='switch']");
        Assert.Equal(2, switches.Count);
        Assert.All(switches, s => Assert.False(s.HasAttribute("disabled")));

        // R1-52: the green outline of a valid modified field must not frame the switch (a button).
        var css = File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));
        Assert.Contains(".valid.modified:not([type=checkbox]):not(.omni-switch)", css, StringComparison.Ordinal);
    }
}
