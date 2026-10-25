// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Tests.Services;

public class RouteAccessPolicyTests
{
    [Fact]
    public void ProtectedRoute_IsDeniedBeforePermissionsLoad()
    {
        Assert.False(RouteAccessPolicy.CanAccess("servers", true, false, new PermissionService()));
    }

    [Theory]
    [InlineData("servers")]
    [InlineData("projects/42/overview")]
    [InlineData("pipelines/runs/8?tab=logs")]
    [InlineData("analysis")]
    [InlineData("backups")]
    [InlineData("admin/users")]
    [InlineData("unknown-future-page")]
    public void UserWithoutRights_IsDeniedProtectedAndUnknownRoutes(string route)
    {
        var permissions = Loaded();

        Assert.False(RouteAccessPolicy.CanAccess(route, true, false, permissions));
    }

    [Theory]
    [InlineData("")]
    [InlineData("settings/tokens")]
    [InlineData("help/rbac")]
    [InlineData("account/change-password")]
    [InlineData("not-found")]
    [InlineData("notifications")]
    public void AuthenticatedUser_CanAccessPersonalRoutes(string route)
    {
        Assert.True(RouteAccessPolicy.CanAccess(route, true, false, Loaded()));
    }

    [Theory]
    [InlineData("analysis")]
    [InlineData("backups")]
    [InlineData("projects/12/quality")]
    [InlineData("projects/12/backups")]
    public void ProjectReadPermission_OpensAnalysisAndBackupRoutes(string route)
    {
        var permissions = Loaded(new EffectivePermissionDto
        {
            ResourceType = ResourceType.Project,
            ResourceId = 12,
            Permission = Permission.Read
        });

        Assert.True(RouteAccessPolicy.CanAccess(route, true, false, permissions));
    }

    [Theory]
    [InlineData("servers/7/overview", ResourceType.Server, 7)]
    [InlineData("projects/12/overview", ResourceType.Project, 12)]
    [InlineData("pipelines/5", ResourceType.Pipeline, 5)]
    [InlineData("releases/3", ResourceType.Release, 3)]
    public void SpecificReadPermission_OnlyOpensMatchingResource(
        string route,
        ResourceType resourceType,
        int resourceId)
    {
        var permissions = Loaded(new EffectivePermissionDto
        {
            ResourceType = resourceType,
            ResourceId = resourceId,
            Permission = Permission.Read
        });

        Assert.True(RouteAccessPolicy.CanAccess(route, true, false, permissions));
        Assert.False(RouteAccessPolicy.CanAccess(route.Replace(resourceId.ToString(), "999"), true, false, permissions));
    }

    [Theory]
    [InlineData("projects/12/pipelines", ResourceType.Pipeline)]
    [InlineData("projects/12/servers", ResourceType.Server)]
    [InlineData("projects/12/releases", ResourceType.Release)]
    [InlineData("projects/12/libraries", ResourceType.VariableLibrary)]
    [InlineData("projects/12/vaults", ResourceType.Vault)]
    [InlineData("projects/12/environments", ResourceType.Environment)]
    public void ProjectSubresource_UsesItsOwnResourcePermission(string route, ResourceType resourceType)
    {
        var permissions = Loaded(
            new EffectivePermissionDto
            {
                ResourceType = ResourceType.Project,
                ResourceId = 12,
                Permission = Permission.Read
            },
            new EffectivePermissionDto
            {
                ResourceType = resourceType,
                Permission = Permission.Read
            });

        Assert.True(RouteAccessPolicy.CanAccess(route, true, false, permissions));
    }

    [Fact]
    public void ReadPermission_DoesNotOpenCreateRoute()
    {
        var permissions = Loaded(new EffectivePermissionDto
        {
            ResourceType = ResourceType.Project,
            Permission = Permission.Read
        });

        Assert.False(RouteAccessPolicy.CanAccess("projects/new", true, false, permissions));
    }

    [Fact]
    public void Admin_CanAccessAdminRouteEvenIfPermissionBootstrapFailed()
    {
        Assert.True(RouteAccessPolicy.CanAccess("admin/users", true, true, new PermissionService()));
    }

    [Fact]
    public void AnonymousUser_CanOnlyAccessLogin()
    {
        var permissions = new PermissionService();

        Assert.True(RouteAccessPolicy.CanAccess("login", false, false, permissions));
        Assert.False(RouteAccessPolicy.CanAccess("settings", false, false, permissions));
    }

    [Fact]
    public void Login_RemainsVisibleDuringAuthenticatedPermissionBootstrap()
    {
        var permissions = new PermissionService();

        Assert.True(RouteAccessPolicy.CanAccess("login", true, false, permissions));
    }

    // FRONT-022: the task detail (/tasks/{id}) follows Server Read, Supervision (/monitoring) Project
    // Read; each stays closed to a user without that permission, and to the other one.
    [Theory]
    [InlineData("tasks/42", ResourceType.Server, true)]
    [InlineData("tasks/42", ResourceType.Project, false)]
    [InlineData("monitoring", ResourceType.Project, true)]
    [InlineData("monitoring", ResourceType.Server, false)]
    // R-52: one alert (/alerts/{id}) follows Server Read, like the alerts list.
    [InlineData("alerts/4242", ResourceType.Server, true)]
    [InlineData("alerts/4242", ResourceType.Project, false)]
    public void TaskDetailAndSupervision_FollowTheReadPermissionTheirApiChecks(
        string route, ResourceType granted, bool expected)
    {
        var permissions = Loaded(new EffectivePermissionDto
        {
            ResourceType = granted,
            ResourceId = 7,
            Permission = Permission.Read
        });

        Assert.Equal(expected, RouteAccessPolicy.CanAccess(route, true, false, permissions));
        Assert.False(RouteAccessPolicy.CanAccess(route, true, false, Loaded()));
    }

    [Fact]
    public void TaskRoute_WithANonNumericTail_StaysUnclassified()
    {
        var permissions = Loaded(new EffectivePermissionDto
        {
            ResourceType = ResourceType.Server,
            ResourceId = null,
            Permission = Permission.Read
        });

        Assert.False(RouteAccessPolicy.CanAccess("tasks/abc", true, false, permissions));
    }

    private static PermissionService Loaded(params EffectivePermissionDto[] grants)
    {
        var permissions = new PermissionService();
        permissions.SetPermissions([.. grants], false);
        return permissions;
    }
}
