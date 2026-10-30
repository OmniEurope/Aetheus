// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using Aetheus.Front.Components.Organizations;
using Aetheus.Front.Components.Users;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

public sealed class SilentMutationFailureNotificationTests : BunitContext
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler handler;

    public SilentMutationFailureNotificationTests()
        => handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    [Fact]
    public async Task UserOrganizationMutations_NullResponses_ShowErrors()
    {
        handler.SetJsonResponse("api/users/roles", new List<string>());
        handler.SetJsonResponse("api/users/1", new UserDto { Id = 1, Username = "alice" });
        handler.SetJsonResponse(
            "api/users/1/effective-permissions",
            new UserPermissionSummaryDto { UserId = 1, Username = "alice" });
        var cut = Render<UserEdit>(parameters => parameters.Add(component => component.Id, 1));
        cut.WaitForState(() => !(bool)typeof(UserEdit).GetField("_loading", PrivateInstance)!
            .GetValue(cut.Instance)!);
        typeof(UserEdit).GetField("_selectedOrgId", PrivateInstance)!.SetValue(cut.Instance, 7);
        handler.SetResponse(HttpMethod.Post, "api/organizations/7/members", HttpStatusCode.BadRequest);

        await cut.InvokeAsync(() => (Task)typeof(UserEdit)
            .GetMethod("OnAddOrg", PrivateInstance)!.Invoke(cut.Instance, [])!);
        var notifications = Services.GetRequiredService<OmniOverlayService>();
        Assert.Contains(notifications.Toasts(), message => message.Severity == OmniSeverity.Danger);
        Services.ClearToasts();

        var org = new UserOrganizationDto(7, 70, "Org", "org", OrganizationRole.Member);
        handler.SetResponse(HttpMethod.Put, "api/organizations/7/members/70", HttpStatusCode.BadRequest);
        await cut.InvokeAsync(() => (Task)typeof(UserEdit)
            .GetMethod("OnChangeOrgRole", PrivateInstance)!
            .Invoke(cut.Instance, [org, OrganizationRole.Maintainer])!);

        Assert.Contains(notifications.Toasts(), message => message.Severity == OmniSeverity.Danger);
    }

    [Fact]
    public async Task OrganizationGeneralSave_NullResponse_ShowsError()
    {
        handler.SetJsonResponse("api/organizations/2", new OrganizationDetailDto(
            2, "Org", "org", "desc", DateTime.UtcNow, DateTime.UtcNow, [], []));
        handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>());
        handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>());
        var cut = Render<OrganizationDetail>(parameters => parameters.Add(component => component.Id, 2));
        cut.WaitForState(() => !(bool)typeof(OrganizationDetail).GetField("_loading", PrivateInstance)!
            .GetValue(cut.Instance)!);
        handler.SetResponse(HttpMethod.Put, "api/organizations/2", HttpStatusCode.BadRequest);

        await cut.InvokeAsync(() => (Task)typeof(OrganizationDetail)
            .GetMethod("OnSaveGeneral", PrivateInstance)!.Invoke(cut.Instance, [])!);

        Assert.Contains(
            Services.Toasts(),
            message => message.Severity == OmniSeverity.Danger);
    }

    [Fact]
    public async Task AddUserToRole_FailedStatus_ShowsError()
    {
        handler.SetJsonResponse("api/roles/3", new RoleDto { Id = 3, Name = "Editor", Permissions = [] });
        handler.SetJsonResponse("api/audit", new PaginatedResult<AuditLogDto>());
        var cut = Render<RoleEdit>(parameters => parameters.Add(component => component.Id, 3));
        cut.WaitForState(() => !(bool)typeof(RoleEdit).GetField("_loading", PrivateInstance)!
            .GetValue(cut.Instance)!);
        typeof(RoleEdit).GetField("_selectedUserId", PrivateInstance)!.SetValue(cut.Instance, 42);
        // ADR-059: the Editor role is given for an organization (the one the users tab picks).
        typeof(RoleEdit).GetField("_organizationId", PrivateInstance)!.SetValue(cut.Instance, 4);
        handler.SetResponse(HttpMethod.Post, "api/roles/3/users", HttpStatusCode.Conflict);

        await cut.InvokeAsync(() => (Task)typeof(RoleEdit)
            .GetMethod("OnAddUser", PrivateInstance)!.Invoke(cut.Instance, [])!);

        Assert.Contains(
            Services.Toasts(),
            message => message.Severity == OmniSeverity.Danger);
    }
}
