// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Users;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Users;

/// <summary>
/// R-10 (d): an organization's administrator creates an account in its organizations only, the choice hidden
/// when there is one, with the roles it gives there; a built-in role it opens is plainly read-only. The backend
/// decides (OrganizationAdminUserCreation integration tests); these tests prove what the UI offers and sends.
/// </summary>
public class OrganizationAdministratorUserCreateTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    private BunitTestHelper.TestHandler RegisterOrganizationAdministrator(params AdministeredOrganizationDto[] administered)
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: false,
            extraPermissionKeys: [PermissionKeys.OrganizationMembersManage]);
        handler.SetJsonResponse("api/organizations/administered", administered.ToList());
        handler.SetJsonResponse("api/roles/assignable", new PaginatedResult<RoleDto>
        {
            Items = [new RoleDto { Id = 3, Name = RoleNames.Reader }, new RoleDto { Id = 9, Name = "acme-auditor", OrganizationId = 1 }],
            TotalCount = 2
        });
        return handler;
    }

    private static AdministeredOrganizationDto Org(int id, string name, params string[] keys) => new(id, name, keys);

    [Fact]
    public void OneOrganization_TheChoiceIsHidden_AndTheRolesOfferedAreThoseItGivesThere()
    {
        var handler = RegisterOrganizationAdministrator(Org(1, "Acme", PermissionKeys.OrganizationMembersManage));

        var cut = Render<UserCreate>();

        cut.WaitForAssertion(() => Assert.Contains(handler.RequestsSnapshot(), request =>
            request.Url.Contains("api/roles/assignable", StringComparison.Ordinal) && request.Url.Contains("organizationId=1", StringComparison.Ordinal)));
        Assert.Empty(cut.FindAll("#role-assignment-organization"));
        Assert.DoesNotContain("RoleAssignmentNoOrganization", cut.Markup);
        // Never the site Admin's sources: every role, every organization.
        Assert.DoesNotContain(handler.RequestsSnapshot(), request => request.Url.Contains("api/roles?", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.RequestsSnapshot(), request => request.Url.Contains("api/organizations?", StringComparison.Ordinal));
    }

    [Fact]
    public void SeveralOrganizations_TheChoiceListsOnlyThoseHoldingTheMembersKey()
    {
        RegisterOrganizationAdministrator(
            Org(1, "Acme", PermissionKeys.OrganizationMembersManage),
            Org(2, "Globex", PermissionKeys.OrganizationRolesManage),
            Org(3, "Initech", PermissionKeys.OrganizationMembersManage));

        var cut = Render<UserCreate>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#role-assignment-organization")));
        var choice = cut.FindComponent<OmniDropDown<int>>();
        Assert.Equal(["Acme", "Initech"], choice.Instance.Options!.Select(option => option.Text));
    }

    [Fact]
    public async Task Submitting_SendsTheRoleForItsOrganization_AndNoSiteWideRole()
    {
        var handler = RegisterOrganizationAdministrator(Org(1, "Acme", PermissionKeys.OrganizationMembersManage));
        handler.SetJsonResponse(HttpMethod.Post, "api/users", new UserDto
        {
            Id = 12,
            Username = "jdoe",
            WelcomeMail = new WelcomeMailResultDto { Status = WelcomeMailStatus.Sent, To = "jdoe@example.org" }
        });
        var cut = Render<UserCreate>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindComponents<RoleAssignmentPicker>()));
        var picker = cut.FindComponent<RoleAssignmentPicker>();
        await cut.InvokeAsync(async () => await (Task)typeof(RoleAssignmentPicker).GetMethod("OnRoleToggledAsync", Priv)!
            .Invoke(picker.Instance, [RoleNames.Reader, true])!);
        var model = typeof(UserCreate).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Username")!.SetValue(model, "jdoe");
        model.GetType().GetProperty("Email")!.SetValue(model, "jdoe@example.org");

        await cut.InvokeAsync(async () => await (Task)typeof(UserCreate).GetMethod("OnSubmit", Priv)!.Invoke(cut.Instance, [])!);

        var body = Assert.Single(handler.RequestDetails, request => request.Method == "POST" && request.Url.Contains("api/users")).Body!;
        var sent = System.Text.Json.JsonSerializer.Deserialize<CreateUserRequest>(body, JsonOptions.Web)!;
        Assert.Empty(sent.Roles);
        var held = Assert.Single(sent.OrganizationRoles);
        Assert.Equal((1, RoleNames.Reader), (held.OrganizationId, held.Role));
        // The account page is the site Admin's: back to the users' list.
        Assert.EndsWith("/admin/users", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void AUserWithoutTheMembersKey_IsSentHome()
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: false,
            extraPermissionKeys: [PermissionKeys.OrganizationRolesManage]);

        Render<UserCreate>();

        Assert.EndsWith("/", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.DoesNotContain(handler.RequestsSnapshot(), request => request.Url.Contains("api/roles", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("general")]
    [InlineData("access")]
    [InlineData("permissions")]
    public void ABuiltInRole_OpenedByAnOrganizationAdministrator_IsPlainlyReadOnly_OnEveryTab(string tab)
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: false,
            extraPermissionKeys: [PermissionKeys.OrganizationRolesManage]);
        handler.SetJsonResponse("api/roles/3", new RoleDto
        {
            Id = 3,
            Name = RoleNames.Reader,
            Description = "Read access",
            OrganizationId = null,
            Permissions = [new ResourcePermissionDto { ResourceType = ResourceType.Server, Permission = Permission.Read }],
            PermissionKeys = [PermissionKeys.ServerView]
        });
        handler.SetJsonResponse("api/roles/3/permission-keys", new List<string> { PermissionKeys.ServerView });
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/admin/roles/3?tab={tab}");

        var cut = Render<RoleEdit>(parameters => parameters.Add(component => component.Id, 3));

        cut.WaitForState(() => !(bool)typeof(RoleEdit).GetField("_loading", Priv)!.GetValue(cut.Instance)!);
        Assert.Contains("BuiltInRoleReadOnly", cut.Find(".role-built-in-note").TextContent);
        switch (tab)
        {
            case "general":
                Assert.True(cut.Find("#oe-pages-users-roleedit-1").HasAttribute("readonly"));
                Assert.True(cut.Find("#oe-pages-users-roleedit-2").HasAttribute("readonly"));
                Assert.Empty(cut.FindAll("button[type=submit]"));
                break;
            case "access":
                Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Save", StringComparison.Ordinal));
                break;
            default:
                Assert.Empty(cut.FindAll(".bulk-perm-select"));
                Assert.All(cut.FindComponents<OmniCheckBox<bool>>(), box => Assert.True(box.Instance.Disabled));
                break;
        }
    }
}
