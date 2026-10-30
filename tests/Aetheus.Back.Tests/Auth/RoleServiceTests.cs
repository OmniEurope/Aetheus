// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Users;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class RoleServiceTests
{
    private readonly IRoleRepository _repoMock = Substitute.For<IRoleRepository>();
    private readonly IUserService _userServiceMock = Substitute.For<IUserService>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly IUserChangeNotifier _userNotifierMock = Substitute.For<IUserChangeNotifier>();
    private readonly RoleService _sut;

    public RoleServiceTests()
    {
        _authzMock.HasPermissionAsync(
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
                Arg.Any<ResourceType>(),
                Arg.Any<int?>(),
                Arg.Any<Permission>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.GetUsersInRoleAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetEffectiveActionKeysAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _sut = new RoleService(_repoMock, _userServiceMock, _auditMock, _authzMock, Substitute.For<IAdminChangeNotifier>(), _userNotifierMock);
    }

    [Fact]
    public async Task GetRolesAsync_ReturnsPaginatedRoles()
    {
        _repoMock.GetRolesPagedAsync(
                "adm", 2, 10, "Name", true, Arg.Any<CancellationToken>())
            .Returns((new List<RoleDto> { new() { Id = 1, Name = "Admin" } }, 11));

        var result = await _sut.GetRolesAsync(new PaginationRequest
        {
            Search = "adm",
            Page = 2,
            PageSize = 10,
            SortBy = "Name",
            SortDescending = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.Equal("Admin", result.Items[0].Name);
        Assert.Equal(11, result.TotalCount);
        Assert.Equal(2, result.Page);
        Assert.Equal(10, result.PageSize);
    }

    [Fact]
    public async Task GetRoleAsync_Found_ReturnsDto()
    {
        _repoMock.GetRoleAsync(1, Arg.Any<CancellationToken>())
            .Returns(new RoleDto { Id = 1, Name = "Developer" });

        var result = await _sut.GetRoleAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Developer", result.Name);
    }

    [Fact]
    public async Task GetRoleAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetRoleAsync(99, Arg.Any<CancellationToken>())
            .Returns((RoleDto?)null);

        var result = await _sut.GetRoleAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateRoleAsync_DuplicateName_ThrowsConflict()
    {
        _repoMock.RoleNameTakenAsync("Admin", null, null, Arg.Any<CancellationToken>())
            .Returns(true);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.CreateRoleAsync(new CreateRoleRequest { Name = "Admin", Description = "Admin role" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateRoleAsync_Valid_CreatesAndAudits()
    {
        _repoMock.RoleNameTakenAsync("Developer", null, null, Arg.Any<CancellationToken>())
            .Returns(false);
        _repoMock.CreateRoleAsync("Developer", "Dev role", Arg.Any<CancellationToken>())
            .Returns(new Role { Id = 2, Name = "Developer", Description = "Dev role" });

        var result = await _sut.CreateRoleAsync(new CreateRoleRequest { Name = "Developer", Description = "Dev role" }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("Developer", result.Name);
        await _auditMock.Received(1).LogAsync("Role.Created", "Role", 2, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateRoleAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetRoleEntityAsync(99, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

        var result = await _sut.UpdateRoleAsync(99, new UpdateRoleRequest { Name = "X", Description = "" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateRoleAsync_RenameProtectedRole_ThrowsBadRequest()
    {
        _repoMock.GetRoleEntityAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = 1, Name = "Admin", Description = "Admin" });

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.UpdateRoleAsync(1, new UpdateRoleRequest { Name = "NewName", Description = "" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateRoleAsync_DuplicateName_ThrowsConflict()
    {
        _repoMock.GetRoleEntityAsync(2, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = 2, Name = "Dev", Description = "" });
        _repoMock.RoleNameTakenAsync("Existing", null, 2, Arg.Any<CancellationToken>())
            .Returns(true);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.UpdateRoleAsync(2, new UpdateRoleRequest { Name = "Existing", Description = "" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateRoleAsync_Valid_UpdatesAndAudits()
    {
        var role = new Role { Id = 2, Name = "Dev", Description = "old" };
        _repoMock.GetRoleEntityAsync(2, Arg.Any<CancellationToken>()).Returns(role);
        _repoMock.RoleNameTakenAsync("Developer", null, 2, Arg.Any<CancellationToken>()).Returns(false);
        _repoMock.UpdateRoleAsync(role, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repoMock.GetRoleAsync(2, Arg.Any<CancellationToken>())
            .Returns(new RoleDto { Id = 2, Name = "Developer", Description = "new" });

        var result = await _sut.UpdateRoleAsync(2, new UpdateRoleRequest { Name = "Developer", Description = "new" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Developer", result.Name);
        await _auditMock.Received(1).LogAsync("Role.Updated", "Role", 2, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRoleAsync_NotFound_ReturnsFalse()
    {
        _repoMock.GetRoleEntityAsync(99, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

        var result = await _sut.DeleteRoleAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteRoleAsync_ProtectedRole_ThrowsBadRequest()
    {
        _repoMock.GetRoleEntityAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = 1, Name = "Admin", Description = "Admin" });

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.DeleteRoleAsync(1, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteRoleAsync_Valid_DeletesAuditsAndNotifiesMembers()
    {
        var role = new Role { Id = 3, Name = "Tester", Description = "" };
        _repoMock.GetRoleEntityAsync(3, Arg.Any<CancellationToken>()).Returns(role);
        _repoMock.GetUsersInRoleAsync(3, Arg.Any<CancellationToken>())
            .Returns([new RoleUserDto(9, "bob", "b@x.io", true)]);
        _repoMock.DeleteRoleAsync(role, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.DeleteRoleAsync(3, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _auditMock.Received(1).LogAsync("Role.Deleted", "Role", 3, Arg.Any<string>(), Arg.Any<CancellationToken>());
        _authzMock.Received(1).InvalidateRoleCache("bob");
        await _userNotifierMock.Received(1).NotifyPermissionsChangedAsync(9, "RolePermissions", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUsersInRoleAsync_NotFound_ThrowsNotFound()
    {
        _repoMock.GetRoleEntityAsync(99, Arg.Any<CancellationToken>()).Returns((Role?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetUsersInRoleAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetUsersInRoleAsync_Found_ReturnsUsers()
    {
        _repoMock.GetRoleEntityAsync(2, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = 2, Name = "Dev", Description = "" });
        _repoMock.GetUsersInRoleAsync(2, Arg.Any<CancellationToken>())
            .Returns([new RoleUserDto(7, "alice", "a@x.io", true)]);

        var result = await _sut.GetUsersInRoleAsync(2, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("alice", result[0].Username);
    }

    [Fact]
    public async Task GetUsersInRoleAsync_Paged_ReturnsRepositoryPage()
    {
        _repoMock.GetRoleEntityAsync(2, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = 2, Name = "Dev", Description = "" });
        _repoMock.GetUsersInRolePagedAsync(
                2, "ali", 2, 10, "Username", true, Arg.Any<CancellationToken>())
            .Returns((new List<RoleUserDto> { new(7, "alice", "a@x.io", true) }, 11));

        var result = await _sut.GetUsersInRoleAsync(2, new PaginationRequest
        {
            Search = "ali",
            Page = 2,
            PageSize = 10,
            SortBy = "Username",
            SortDescending = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("alice", Assert.Single(result.Items).Username);
        Assert.Equal(11, result.TotalCount);
        Assert.Equal(2, result.Page);
        Assert.Equal(10, result.PageSize);
    }

    [Fact]
    public async Task GetUsersAvailableForRoleAsync_NotFound_ThrowsNotFound()
    {
        _repoMock.GetRoleEntityAsync(99, Arg.Any<CancellationToken>()).Returns((Role?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.GetUsersAvailableForRoleAsync(99, new PaginationRequest(), ct: TestContext.Current.CancellationToken));

        await _repoMock.DidNotReceive().GetUsersAvailableForRolePagedAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddUserToRoleAsync_NotFound_ThrowsNotFound()
    {
        _repoMock.GetRoleEntityAsync(99, Arg.Any<CancellationToken>()).Returns((Role?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.AddUserToRoleAsync(99, 1, 4, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddUserToRoleAsync_Valid_DelegatesAndAudits()
    {
        _repoMock.GetRoleEntityAsync(2, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = 2, Name = "Dev", Description = "" });

        await _sut.AddUserToRoleAsync(2, 7, 4, ct: TestContext.Current.CancellationToken);

        await _userServiceMock.Received(1).AssignRoleAsync(7, 2, 4, Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("Role.UserAdded", "Role", 2, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveUserFromRoleAsync_NotInRole_ThrowsNotFound()
    {
        _repoMock.GetRoleEntityAsync(2, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = 2, Name = "Dev", Description = "" });
        _userServiceMock.UnassignRoleAsync(7, 2, 4, Arg.Any<CancellationToken>()).Returns(false);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.RemoveUserFromRoleAsync(2, 7, 4, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveUserFromRoleAsync_Valid_DelegatesAndAudits()
    {
        _repoMock.GetRoleEntityAsync(2, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = 2, Name = "Dev", Description = "" });
        _userServiceMock.UnassignRoleAsync(7, 2, 4, Arg.Any<CancellationToken>()).Returns(true);

        await _sut.RemoveUserFromRoleAsync(2, 7, 4, ct: TestContext.Current.CancellationToken);

        await _userServiceMock.Received(1).UnassignRoleAsync(7, 2, 4, Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("Role.UserRemoved", "Role", 2, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetPermissionsForRoleAsync_NotFound_ThrowsNotFound()
    {
        _repoMock.GetRoleEntityAsync(99, Arg.Any<CancellationToken>())
            .Returns((Role?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.SetPermissionsForRoleAsync(99, new SetResourcePermissionsRequest { Permissions = [] }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetPermissionsForRoleAsync_Valid_SetsAndAudits()
    {
        _repoMock.GetRoleEntityAsync(2, Arg.Any<CancellationToken>())
            .Returns(new Role { Id = 2, Name = "Dev", Description = "" });
        _repoMock.SetPermissionsForRoleAsync(2, Arg.Any<List<ResourcePermissionEntry>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.GetUsersInRoleAsync(2, Arg.Any<CancellationToken>())
            .Returns([new RoleUserDto(7, "alice", "a@x.io", true)]);

        var request = new SetResourcePermissionsRequest
        {
            Permissions = [new ResourcePermissionEntry { ResourceType = ResourceType.Pipeline, Permission = Permission.Read }]
        };

        await _sut.SetPermissionsForRoleAsync(2, request, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).SetPermissionsForRoleAsync(2, Arg.Any<List<ResourcePermissionEntry>>(), Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("Role.PermissionsUpdated", "Role", 2, Arg.Any<string>(), Arg.Any<CancellationToken>());
        _authzMock.Received(1).InvalidateRoleCache("alice");
        await _userNotifierMock.Received(1).NotifyPermissionsChangedAsync(7, "RolePermissions", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloneRoleAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetRoleAsync(99, Arg.Any<CancellationToken>())
            .Returns((RoleDto?)null);

        var result = await _sut.CloneRoleAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task CloneRoleAsync_Valid_ClonesWithPermissions()
    {
        _repoMock.GetRoleAsync(1, Arg.Any<CancellationToken>())
            .Returns(new RoleDto { Id = 1, Name = "Developer", Description = "Dev" });
        _repoMock.RoleNameTakenAsync("Developer (Copy)", null, null, Arg.Any<CancellationToken>())
            .Returns(false);
        _repoMock.CreateRoleAsync("Developer (Copy)", "Dev", Arg.Any<CancellationToken>())
            .Returns(new Role { Id = 5, Name = "Developer (Copy)", Description = "Dev" });
        _repoMock.GetPermissionsForRoleAsync(1, Arg.Any<CancellationToken>())
            .Returns([new ResourcePermissionDto { ResourceType = ResourceType.Pipeline, Permission = Permission.Read }]);
        _repoMock.SetPermissionsForRoleAsync(5, Arg.Any<List<ResourcePermissionEntry>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repoMock.GetRoleAsync(5, Arg.Any<CancellationToken>())
            .Returns(new RoleDto { Id = 5, Name = "Developer (Copy)", Description = "Dev" });

        var result = await _sut.CloneRoleAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Developer (Copy)", result.Name);
        await _auditMock.Received(1).LogAsync("Role.Cloned", "Role", 5, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloneRoleAsync_NameConflict_IncrementsCounter()
    {
        _repoMock.GetRoleAsync(1, Arg.Any<CancellationToken>())
            .Returns(new RoleDto { Id = 1, Name = "Dev", Description = "" });
        _repoMock.RoleNameTakenAsync("Dev (Copy)", null, null, Arg.Any<CancellationToken>())
            .Returns(true);
        _repoMock.RoleNameTakenAsync("Dev (Copy 2)", null, null, Arg.Any<CancellationToken>())
            .Returns(false);
        _repoMock.CreateRoleAsync("Dev (Copy 2)", "", Arg.Any<CancellationToken>())
            .Returns(new Role { Id = 6, Name = "Dev (Copy 2)", Description = "" });
        _repoMock.GetPermissionsForRoleAsync(1, Arg.Any<CancellationToken>())
            .Returns([]);
        _repoMock.GetRoleAsync(6, Arg.Any<CancellationToken>())
            .Returns(new RoleDto { Id = 6, Name = "Dev (Copy 2)", Description = "" });

        var result = await _sut.CloneRoleAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Dev (Copy 2)", result.Name);
    }

    [Fact]
    public async Task GetEffectivePermissionsAsync_UserNotFound_ReturnsNull()
    {
        _userServiceMock.GetUserDetailAsync(99, Arg.Any<CancellationToken>())
            .Returns((UserDto?)null);

        var result = await _sut.GetEffectivePermissionsAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetEffectivePermissionsAsync_Found_ReturnsSummary()
    {
        _userServiceMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new UserDto { Id = 1, Username = "alice", Roles = ["Dev"] });
        _repoMock.GetEffectivePermissionsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new EffectivePermissionDto { ResourceType = ResourceType.Pipeline, Permission = Permission.Admin }]);

        var result = await _sut.GetEffectivePermissionsAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("alice", result.Username);
        Assert.Single(result.EffectivePermissions);
    }

    [Fact]
    public async Task GetMyPermissionsAsync_ReturnsPermissions()
    {
        _userServiceMock.GetCurrentUserAsync(Arg.Any<CancellationToken>())
            .Returns(new UserDto { Id = 1, Username = "alice", Roles = ["Admin"] });
        _repoMock.GetEffectivePermissionsAsync(1, Arg.Any<CancellationToken>())
            .Returns([new EffectivePermissionDto { ResourceType = ResourceType.Server }]);

        var result = await _sut.GetMyPermissionsAsync(UserPrincipal("1", "alice"), ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Single(result.EffectivePermissions);
    }

    [Fact]
    public async Task GetMyPermissionsAsync_ReportsTheRolesHeldPerOrganization()
    {
        // ADR-059 (R-45): membership alone lends nothing; the summary is the roles held per organization,
        // each permission naming the organization it applies to.
        _userServiceMock.GetCurrentUserAsync(Arg.Any<CancellationToken>())
            .Returns(new UserDto { Id = 1, Username = "alice", Roles = [] });
        _repoMock.GetEffectivePermissionsAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new EffectivePermissionDto
            {
                ResourceType = ResourceType.Project, Permission = Permission.Read,
                GrantedByRole = RoleNames.Reader, OrganizationId = 4, OrganizationName = "A"
            }
        ]);

        var result = await _sut.GetMyPermissionsAsync(UserPrincipal("1", "alice"), ct: TestContext.Current.CancellationToken);

        var permission = Assert.Single(result!.EffectivePermissions);
        Assert.Equal(4, permission.OrganizationId);
        Assert.Equal(RoleNames.Reader, permission.GrantedByRole);
        // R-11: the same grant is reported as its catalogue key, in the same organization.
        var key = Assert.Single(result.PermissionKeys);
        Assert.Equal(new EffectivePermissionKeyDto(PermissionKeys.ProjectView, null, 4), key);
    }

    [Fact]
    public async Task R11_GetMyPermissionsAsync_AddsTheActionKeysHeldToTheLevelKeys()
    {
        _userServiceMock.GetCurrentUserAsync(Arg.Any<CancellationToken>())
            .Returns(new UserDto { Id = 1, Username = "alice", Roles = [] });
        _repoMock.GetEffectivePermissionsAsync(1, Arg.Any<CancellationToken>()).Returns(
            [new EffectivePermissionDto { ResourceType = ResourceType.Server, Permission = Permission.Write, OrganizationId = 4 }]);
        _repoMock.GetEffectiveActionKeysAsync(1, Arg.Any<CancellationToken>())
            .Returns([new EffectivePermissionKeyDto(PermissionKeys.ServerFirewallManage, null, 4)]);

        var result = await _sut.GetMyPermissionsAsync(UserPrincipal("1", "alice"), ct: TestContext.Current.CancellationToken);

        Assert.Equal(
            [PermissionKeys.ServerView, PermissionKeys.ServerEdit, PermissionKeys.ServerFirewallManage],
            result!.PermissionKeys.Select(k => k.Key).ToList());
    }

    [Fact]
    public async Task R11_GetMyPermissionsAsync_TheSiteAdminHoldsEveryCatalogueKey()
    {
        _userServiceMock.GetCurrentUserAsync(Arg.Any<CancellationToken>())
            .Returns(new UserDto { Id = 1, Username = "root", Roles = [RoleNames.Admin] });
        _repoMock.GetEffectivePermissionsAsync(1, Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.GetMyPermissionsAsync(UserPrincipal("1", "root", RoleNames.Admin), ct: TestContext.Current.CancellationToken);

        Assert.Equal(PermissionCatalog.All.Select(d => d.Key).Order(StringComparer.Ordinal),
            result!.PermissionKeys.Select(k => k.Key).Order(StringComparer.Ordinal));
    }

    private static System.Security.Claims.ClaimsPrincipal UserPrincipal(string subject, string name, params string[] roles)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, subject),
            new(System.Security.Claims.ClaimTypes.Name, name)
        };
        claims.AddRange(roles.Select(role => new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)));
        return new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "Bearer"));
    }

    [Fact]
    public async Task R2_014_TheBootstrapIdentity_GetsTheSummaryOfItsRoleClaims_NotA404()
    {
        // The deployment smoke test signs in as the run-scoped bootstrap identity, which has no user row.
        _repoMock.GetEffectivePermissionsForRolesAsync(
                Arg.Is<IReadOnlyCollection<string>>(names => names.SequenceEqual(new[] { "Admin" })),
                Arg.Any<CancellationToken>())
            .Returns([new EffectivePermissionDto { ResourceType = ResourceType.Server, Permission = Permission.Admin, GrantedByRole = "Admin" }]);

        var result = await _sut.GetMyPermissionsAsync(
            UserPrincipal("bootstrap", "aetheus-deploy-2471", "Admin"), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(0, result.UserId);
        Assert.Equal("aetheus-deploy-2471", result.Username);
        Assert.Equal(["Admin"], result.Roles);
        var permission = Assert.Single(result.EffectivePermissions);
        Assert.Equal("Admin", permission.GrantedByRole);
        await _userServiceMock.DidNotReceive().GetCurrentUserAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R2_014_TheBootstrapIdentity_GainsNothingFromAUserRowOfTheSameName()
    {
        // Only its claims count: a user row named like it, or organization membership, lends it nothing.
        _userServiceMock.GetCurrentUserAsync(Arg.Any<CancellationToken>())
            .Returns(new UserDto { Id = 9, Username = "admin", Roles = ["Admin", "Operators"] });

        var result = await _sut.GetMyPermissionsAsync(UserPrincipal("bootstrap", "admin"), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result.Roles);
        Assert.Empty(result.EffectivePermissions);
        await _repoMock.DidNotReceive().GetEffectivePermissionsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _repoMock.DidNotReceive().GetEffectivePermissionsForRolesAsync(
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R2_014_AnAccountWhoseRowIsGone_StillGetsNoSummary()
    {
        _userServiceMock.GetCurrentUserAsync(Arg.Any<CancellationToken>()).Returns((UserDto?)null);

        Assert.Null(await _sut.GetMyPermissionsAsync(UserPrincipal("12", "deleted"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetPermissionsForRoleAsync_ReturnsPermissions()
    {
        _repoMock.GetPermissionsForRoleAsync(1, Arg.Any<CancellationToken>())
            .Returns([new ResourcePermissionDto { ResourceType = ResourceType.Pipeline, Permission = Permission.Write }]);

        var result = await _sut.GetPermissionsForRoleAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
    }
}
