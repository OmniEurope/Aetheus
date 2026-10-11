// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

public class UserEditTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public UserEditTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private void SetupRoles(params string[] roles) =>
        _handler.SetPaginatedJsonResponse("api/roles",
            roles.Select((role, index) => new RoleDto
            {
                Id = index + 1,
                Name = role,
                Description = $"{role} description"
            }));

    private void SetupMocks(int id = 1)
    {
        SetupRoles("Admin", "Viewer", "Editor");
        _handler.SetJsonResponse($"api/users/{id}", new UserDto
        {
            Id = id,
            Username = "testuser",
            Email = "test@example.com",
            IsActive = true,
            Roles = ["Viewer"]
        });
        _handler.SetJsonResponse($"api/users/{id}/effective-permissions", new UserPermissionSummaryDto
        {
            EffectivePermissions =
            [
                new EffectivePermissionDto { ResourceType = ResourceType.Server, Permission = Permission.Read }
            ]
        });
        _handler.SetJsonResponse("api/users", new UserDto { Id = id, Username = "testuser" });
    }

    [Fact]
    public void NewUser_RendersEmptyForm()
    {
        SetupRoles("Admin", "Viewer");
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, null));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"), TimeSpan.FromSeconds(2));

        // New-user mode pages through available roles but never fetches a user detail.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("api/roles"));
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("/effective-permissions"));
    }

    [Fact]
    public void EditUser_LoadsExistingData()
    {
        SetupMocks();
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"), TimeSpan.FromSeconds(2));

        // The existing user is fetched and its username is mapped into the form model.
        Assert.Contains(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("api/users/1"));
        var model = typeof(UserEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal("testuser", (string)model.GetType().GetProperty("Username")!.GetValue(model)!);
    }

    [Fact]
    public void NonAdmin_RedirectsToHome()
    {
        var handler = BunitTestHelper.RegisterServices(this, isAdmin: false);
        Render<UserEdit>(p => p.Add(x => x.Id, 1));

        // The non-admin guard returns before any roles/user data is fetched.
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("api/roles"));
    }

    [Fact]
    public void EditUser_WithMultipleRoles_LoadsCorrectly()
    {
        SetupRoles("Admin", "Viewer", "Editor");
        _handler.SetJsonResponse("api/users/2", new UserDto
        {
            Id = 2,
            Username = "admin-user",
            Email = "admin@example.com",
            IsActive = true,
            Roles = ["Admin", "Editor"]
        });
        _handler.SetJsonResponse("api/users/2/permissions", new UserPermissionSummaryDto
        {
            EffectivePermissions = []
        });

        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 2));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"), TimeSpan.FromSeconds(2));

        // Both assigned roles are loaded into the form model.
        var model = typeof(UserEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        var roles = (List<string>)model.GetType().GetProperty("Roles")!.GetValue(model)!;
        Assert.Contains("Admin", roles);
        Assert.Contains("Editor", roles);
    }

    [Fact]
    public void EditUser_InactiveUser_LoadsCorrectly()
    {
        SetupRoles("Viewer");
        _handler.SetJsonResponse("api/users/3", new UserDto
        {
            Id = 3,
            Username = "inactive-user",
            Email = null,
            IsActive = false,
            Roles = []
        });
        _handler.SetJsonResponse("api/users/3/permissions", new UserPermissionSummaryDto
        {
            EffectivePermissions = []
        });

        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 3));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"), TimeSpan.FromSeconds(2));

        // The inactive flag is carried into the form model.
        var model = typeof(UserEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.False((bool)model.GetType().GetProperty("IsActive")!.GetValue(model)!);
    }

    [Fact]
    public async Task ExistingUser_RoleClick_SavesImmediatelyWithoutSaveButton()
    {
        SetupMocks();
        _handler.SetJsonResponse(HttpMethod.Put, "api/users/1", new UserDto
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            IsActive = true,
            Roles = ["Viewer", "Admin"]
        });
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/users/1?tab=roles");
        var cut = Render<UserEdit>(parameters => parameters.Add(component => component.Id, 1));
        cut.WaitForState(() => cut.FindAll(".role-assignment-picker").Count == 1);

        var roleCard = cut.Find(".iam-role-assignment-card");
        Assert.DoesNotContain(roleCard.QuerySelectorAll("button"), button =>
            string.Equals(button.TextContent.Trim(), "Save", StringComparison.OrdinalIgnoreCase));

        var picker = cut.FindComponent<RoleAssignmentPicker>();
        await cut.InvokeAsync(() =>
            picker.Instance.SelectedRolesChanged.InvokeAsync(["Viewer", "Admin"]));

        var request = Assert.Single(_handler.RequestDetails, request =>
            request.Method == "PUT" && request.Url.Contains("api/users/1"));
        Assert.Contains("\"Admin\"", request.Body);
        cut.WaitForAssertion(() => Assert.Contains("Saved", roleCard.TextContent));
    }

    [Fact]
    public async Task ExistingUser_RoleSave_PreservesSavedRolesWhenPermissionRefreshIsUnauthorized()
    {
        SetupMocks();
        _handler.SetJsonResponse("api/users/1", new UserDto
        {
            Id = 1,
            Username = "admin",
            Email = "admin@example.com",
            IsActive = true,
            Roles = ["Viewer"]
        });
        _handler.SetJsonResponse(HttpMethod.Put, "api/users/1", new UserDto
        {
            Id = 1,
            Username = "admin",
            Email = "admin@example.com",
            IsActive = true,
            Roles = ["Viewer", "Admin"]
        });
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/users/1?tab=roles");
        var cut = Render<UserEdit>(parameters => parameters.Add(component => component.Id, 1));
        cut.WaitForState(() => cut.FindAll(".role-assignment-picker").Count == 1);
        _handler.SetResponse(
            HttpMethod.Get,
            "api/users/1/effective-permissions",
            System.Net.HttpStatusCode.Unauthorized);

        var picker = cut.FindComponent<RoleAssignmentPicker>();
        await cut.InvokeAsync(() =>
            picker.Instance.SelectedRolesChanged.InvokeAsync(["Viewer", "Admin"]));

        var model = typeof(UserEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        var roles = (List<string>)model.GetType().GetProperty("Roles")!.GetValue(model)!;
        Assert.Equal(["Viewer", "Admin"], roles);
        Assert.True((bool)typeof(UserEdit).GetField("_rolesSaved", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!);
        Assert.Contains(Services.Toasts(),
            message => message.Severity == OmniSeverity.Warning
                       && message.Detail == "UserRolesSavedPermissionRefreshUnauthorized");
    }

    [Fact]
    public async Task ExistingUser_RoleSave_ReportsUnexpectedPermissionRefreshFailureWithoutRollback()
    {
        SetupMocks();
        _handler.SetJsonResponse(HttpMethod.Put, "api/users/1", new UserDto
        {
            Id = 1,
            Username = "testuser",
            Email = "test@example.com",
            IsActive = true,
            Roles = ["Viewer", "Admin"]
        });
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/users/1?tab=roles");
        var cut = Render<UserEdit>(parameters => parameters.Add(component => component.Id, 1));
        cut.WaitForState(() => cut.FindAll(".role-assignment-picker").Count == 1);
        _handler.SetResponse(
            HttpMethod.Get,
            "api/users/1/effective-permissions",
            System.Net.HttpStatusCode.InternalServerError);

        var picker = cut.FindComponent<RoleAssignmentPicker>();
        await cut.InvokeAsync(() =>
            picker.Instance.SelectedRolesChanged.InvokeAsync(["Viewer", "Admin"]));

        var model = typeof(UserEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        var roles = (List<string>)model.GetType().GetProperty("Roles")!.GetValue(model)!;
        Assert.Equal(["Viewer", "Admin"], roles);
        Assert.Contains(Services.Toasts(),
            message => message.Severity == OmniSeverity.Warning
                       && message.Detail == "UserRolesSavedPermissionRefreshFailed");
    }

    // --- OnSubmit ---

    [Fact]
    public async Task OnSubmit_NewUser_ShortPassword_ShowsError()
    {
        SetupRoles("Viewer");
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, null));

        var model = typeof(UserEdit).GetField("_model", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Password")!.SetValue(model, "ab");

        var method = typeof(UserEdit).GetMethod("OnSubmit", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        // A < 6 char password fails the length guard: the create POST is never sent.
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/users"));
    }

    [Fact]
    public async Task OnSubmit_ExistingUser_UpdatesUser()
    {
        SetupMocks();

        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 1));

        var method = typeof(UserEdit).GetMethod("OnSubmit", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [])!;

        // The edit path issues a PUT to api/users/1 (the update the test name promises).
        Assert.Contains(_handler.Requests, r => r.Method == "PUT" && r.Url.Contains("api/users/1"));
    }

    [Fact]
    public async Task ActiveToggle_DeactivationCancelled_DoesNotUpdateAndRestoresToggle()
    {
        SetupMocks();
        var cut = Render<UserEdit>(parameters => parameters.Add(component => component.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"), TimeSpan.FromSeconds(2));
        var activeToggle = cut.FindComponents<OmniSwitch<bool>>()
            .Single(toggle => toggle.Find("button").TextContent.Contains("Active", StringComparison.Ordinal));
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var change = cut.InvokeAsync(() => activeToggle.Instance.ValueChanged.InvokeAsync(false));
        await cut.InvokeAsync(() => dialog.Close(false));
        await change;

        Assert.DoesNotContain(_handler.Requests, request => request.Method == "PUT");
        Assert.Equal("true", cut.FindAll("button.omni-switch")
            .Single(toggle => toggle.TextContent.Contains("Active", StringComparison.Ordinal))
            .GetAttribute("aria-checked"));
    }

    [Fact]
    public async Task ActiveToggle_DeactivationConfirmed_UpdatesImmediately()
    {
        SetupMocks();
        var cut = Render<UserEdit>(parameters => parameters.Add(component => component.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"), TimeSpan.FromSeconds(2));
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var activeToggle = cut.FindComponents<OmniSwitch<bool>>()
            .Single(toggle => toggle.Find("button").TextContent.Contains("Active", StringComparison.Ordinal));
        var change = cut.InvokeAsync(() => activeToggle.Instance.ValueChanged.InvokeAsync(false));
        await cut.InvokeAsync(() => dialog.Close(true));
        await change;

        Assert.Contains(_handler.Requests, request =>
            request.Method == "PUT" && request.Url.Contains("api/users/1", StringComparison.Ordinal));
    }

    [Fact]
    public void OrganizationsDeepLink_LoadsEveryServerPage()
    {
        SetupMocks();
        _handler.SetJsonResponse("api/users/1/organizations", new List<Aetheus.Shared.Components.Organizations.UserOrganizationDto>());
        _handler.SetJsonResponse("api/organizations?page=1&pageSize=200", new PaginatedResult<Aetheus.Shared.Components.Organizations.OrganizationDto>
        {
            Items = [new Aetheus.Shared.Components.Organizations.OrganizationDto(10, "First", "first", "", 0, 0, default, default)],
            TotalCount = 201,
            Page = 1,
            PageSize = 200
        });
        _handler.SetJsonResponse("api/organizations?page=2&pageSize=200", new PaginatedResult<Aetheus.Shared.Components.Organizations.OrganizationDto>
        {
            Items = [new Aetheus.Shared.Components.Organizations.OrganizationDto(11, "Last", "last", "", 0, 0, default, default)],
            TotalCount = 201,
            Page = 2,
            PageSize = 200
        });
        Services.GetRequiredService<NavigationManager>().NavigateTo("/users/1?tab=organizations");

        var cut = Render<UserEdit>(parameters => parameters.Add(component => component.Id, 1));

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/organizations?page=2&pageSize=200", StringComparison.Ordinal)));
        var organizations = (System.Collections.IList)typeof(UserEdit)
            .GetField("_availableOrgs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        Assert.Equal(2, organizations.Count);
    }

    [Fact]
    public async Task OnDelete_FailedStatusDoesNotNavigate()
    {
        SetupMocks();
        _handler.SetResponse(HttpMethod.Delete, "api/users/1", System.Net.HttpStatusCode.Conflict);
        var cut = Render<UserEdit>(parameters => parameters.Add(component => component.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"), TimeSpan.FromSeconds(2));
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var method = typeof(UserEdit).GetMethod("OnDelete", BindingFlags.NonPublic | BindingFlags.Instance)!;

        var task = cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => dialog.Close(true));
        await task;

        Assert.Contains(_handler.Requests, request =>
            request.Method == "DELETE" && request.Url.EndsWith("api/users/1", StringComparison.Ordinal));
        Assert.False(nav.Uri.EndsWith("/users", StringComparison.Ordinal));
        Assert.Contains(Services.Toasts(),
            message => message.Severity == OmniSeverity.Danger);
    }

    // --- FRONT-024: the account shown is the account acted on ---

    private void SetupAccount(int id, string username)
    {
        _handler.SetJsonResponse(HttpMethod.Get, $"api/users/{id}", new UserDto { Id = id, Username = username, IsActive = true });
        _handler.SetJsonResponse(HttpMethod.Get, $"api/users/{id}/effective-permissions", new UserPermissionSummaryDto());
    }

    private static UserDto? DisplayedUser(IRenderedComponent<UserEdit> cut) =>
        (UserDto?)typeof(UserEdit).GetField("_user", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);

    [Fact]
    public async Task LateAnswerForThePreviousAccount_IsDropped_AndPasswordAndDeleteTargetTheDisplayedOne()
    {
        SetupRoles("Admin");
        var aliceAnswer = new TaskCompletionSource<UserDto>();
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/users/1", _ => aliceAnswer.Task);
        _handler.SetJsonResponse(HttpMethod.Get, "api/users/1/effective-permissions", new UserPermissionSummaryDto());
        SetupAccount(2, "bob");
        _handler.SetResponse(HttpMethod.Post, "api/users/2/change-password", System.Net.HttpStatusCode.NoContent);
        _handler.SetResponse(HttpMethod.Delete, "api/users/2", System.Net.HttpStatusCode.NoContent);
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 1));

        cut.Render(p => p.Add(x => x.Id, 2));
        cut.WaitForAssertion(() => Assert.Equal("bob", DisplayedUser(cut)?.Username));
        aliceAnswer.SetResult(new UserDto { Id = 1, Username = "alice", IsActive = true });
        // Let the late answer travel through the client and back onto the renderer before looking.
        await Task.Delay(300, Xunit.TestContext.Current.CancellationToken);
        await cut.InvokeAsync(() => { });

        Assert.Equal("bob", DisplayedUser(cut)?.Username);
        Assert.DoesNotContain("alice", cut.Markup);
        SetFormValue(cut.Instance, "_passwordChange", "NewPassword", "correct-horse-battery");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "OnChangePassword", "_passwordChange"));
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var delete = cut.InvokeAsync(() => (Task)typeof(UserEdit).GetMethod("OnDelete", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cut.Instance, [])!);
        await cut.InvokeAsync(() => dialog.Close(true));
        await delete;

        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("api/users/2/change-password", StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, r => r.Method == "DELETE" && r.Url.EndsWith("api/users/2", StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.Requests, r => r.Method != "GET" && r.Url.Contains("api/users/1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedLoadOfTheNextAccount_LeavesNothingToActOn()
    {
        SetupRoles("Admin");
        SetupAccount(1, "alice");
        _handler.SetResponse(HttpMethod.Get, "api/users/2", System.Net.HttpStatusCode.InternalServerError);
        _handler.SetJsonResponse(HttpMethod.Get, "api/users/2/effective-permissions", new UserPermissionSummaryDto());
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Equal("alice", DisplayedUser(cut)?.Username));

        cut.Render(p => p.Add(x => x.Id, 2));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.EndsWith("api/users/2", StringComparison.Ordinal)));

        Assert.Null(DisplayedUser(cut));
        Assert.DoesNotContain("alice", cut.Markup);
        var delete = cut.InvokeAsync(() => (Task)typeof(UserEdit).GetMethod("OnDelete", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cut.Instance, [])!);
        await delete;
        SetFormValue(cut.Instance, "_passwordChange", "NewPassword", "correct-horse-battery");
        await cut.InvokeAsync(() => InvokeFormSubmitAsync(cut.Instance, "OnChangePassword", "_passwordChange"));

        Assert.DoesNotContain(_handler.Requests, r => r.Method is "DELETE" or "POST");
    }

    [Fact]
    public async Task DeleteConfirmedAfterTheRouteMovedToAnotherAccount_DeletesNobody()
    {
        SetupRoles("Admin");
        SetupAccount(1, "alice");
        SetupAccount(2, "bob");
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Equal("alice", DisplayedUser(cut)?.Username));
        var dialog = Services.GetRequiredService<OmniDialogService>();
        var delete = cut.InvokeAsync(() => (Task)typeof(UserEdit).GetMethod("OnDelete", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cut.Instance, [])!);

        cut.Render(p => p.Add(x => x.Id, 2));
        cut.WaitForAssertion(() => Assert.Equal("bob", DisplayedUser(cut)?.Username));
        await cut.InvokeAsync(() => dialog.Close(true));
        await delete;

        Assert.DoesNotContain(_handler.Requests, r => r.Method == "DELETE");
    }

    // --- OnChangePassword ---

    [Fact]
    public async Task OnChangePassword_ShortPassword_MakesNoRequest()
    {
        SetupMocks();
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 1));

        SetFormValue(cut.Instance, "_passwordChange", "NewPassword", "ab");
        await InvokeFormSubmitAsync(cut.Instance, "OnChangePassword", "_passwordChange");

        // A < 6 char password fails the length guard - no change-password call is sent.
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("change-password"));
    }

    [Fact]
    public void SecurityPassword_InvalidSubmit_ShowsWarningNotification()
    {
        SetupMocks();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/users/1?tab=security");
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"), TimeSpan.FromSeconds(2));
        cut.Find("input[type='password']").Input("short");
        BunitTestHelper.Interactive(this);

        cut.Find("form").Submit();

        Assert.Contains(Services.Toasts(),
            message => message.Severity == OmniSeverity.Warning);
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("change-password"));
    }

    [Fact]
    public async Task SecurityPassword_NativeInput_UpdatesRuleFeedbackImmediately()
    {
        SetupMocks();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/users/1?tab=security");
        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForState(() => !cut.Markup.Contains("rz-progressbar-circular"), TimeSpan.FromSeconds(2));
        cut.WaitForState(() => cut.FindAll("input[type='password']").Count == 1);

        var password = cut.Find("input[type='password']");
        password.Input("correct-horse-battery");

        cut.WaitForAssertion(() =>
            Assert.Contains("omni-u-text-success", cut.Find(".omni-u-text-success").ClassList));
        Assert.Equal("correct-horse-battery",
            GetFormValue<string>(cut.Instance, "_passwordChange", "NewPassword"));
    }

    // --- GetPermissionBadgeStyle ---

    [Theory]
    [InlineData(Permission.Admin, OmniTone.Danger)]
    [InlineData(Permission.Write, OmniTone.Warning)]
    [InlineData(Permission.Read, OmniTone.Accent)]
    public void GetPermissionBadgeStyle_ReturnsExpected(Permission perm, OmniTone expected)
    {
        var method = typeof(UserEdit).GetMethod("GetPermissionBadgeStyle", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (OmniTone)method.Invoke(null, [perm])!;
        Assert.Equal(expected, result);
    }

    // --- User with permissions ---

    [Fact]
    public void EditUser_WithEffectivePermissions_ShowsPermissionTable()
    {
        SetupRoles("Admin", "Viewer");
        _handler.SetJsonResponse("api/users/4", new UserDto
        {
            Id = 4,
            Username = "permuser",
            IsActive = true,
            Roles = ["Admin"]
        });
        _handler.SetJsonResponse("api/users/4/effective-permissions", new UserPermissionSummaryDto
        {
            EffectivePermissions =
            [
                new EffectivePermissionDto { ResourceType = ResourceType.Server, Permission = Permission.Admin },
                new EffectivePermissionDto { ResourceType = ResourceType.Pipeline, Permission = Permission.Write },
                new EffectivePermissionDto { ResourceType = ResourceType.Project, Permission = Permission.Read }
            ]
        });

        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 4));
        cut.WaitForState(() =>
            typeof(UserEdit).GetField("_effectivePermissions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance) is not null,
            TimeSpan.FromSeconds(2));

        // All three stubbed effective permissions are loaded for the permission table.
        var perms = (List<EffectivePermissionDto>?)typeof(UserEdit)
            .GetField("_effectivePermissions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance);
        Assert.NotNull(perms);
        Assert.Equal(3, perms!.Count);
    }

    [Fact]
    public void OrganizationRowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        SetupMocks();
        _handler.SetJsonResponse("api/organizations", new PaginatedResult<OrganizationDto>());
        _handler.SetJsonResponse("api/users/1/organizations", new List<Aetheus.Shared.Components.Organizations.UserOrganizationDto>
        {
            new(4, 8, "Acme", "acme", OrganizationRole.Member)
        });

        var cut = Render<UserEdit>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='tab']")), TimeSpan.FromSeconds(3));
        cut.FindAll("[role='tab']").First(tab => tab.TextContent.Trim() == "Organizations").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)), TimeSpan.FromSeconds(3));

        // The organization link, the role list and the remove button.
        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 3);
    }
}
