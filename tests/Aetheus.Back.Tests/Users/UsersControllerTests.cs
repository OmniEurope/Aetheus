// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Users;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class UsersControllerTests
{
    private readonly IUserService _serviceMock = Substitute.For<IUserService>();
    private readonly UsersController _sut;

    public UsersControllerTests()
    {
        // The site Admin administers every organization (null), as the real service answers for the Admin role.
        var authz = Substitute.For<IResourceAuthorizationService>();
        authz.GetOrganizationsWithPermissionKeyAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<int>?)null);
        _sut = new UsersController(_serviceMock, authz, Substitute.For<IOrganizationRoleAssignmentService>());
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, "Admin")], "test"))
            }
        };
    }

    [Fact]
    public async Task GetUsers_ReturnsOkWithPaginatedResult()
    {
        _serviceMock.GetUsersAsync(Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<UserDto> { Items = [new UserDto { Id = 1, Username = "admin" }], TotalCount = 1 });

        var result = await _sut.GetUsers(new PaginationRequest(), null, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<PaginatedResult<UserDto>>(ok.Value);
    }

    [Fact]
    public async Task GetUser_Found_ReturnsOk()
    {
        _serviceMock.GetUserDetailAsync(1, Arg.Any<CancellationToken>())
            .Returns(new UserDto { Id = 1, Username = "admin" });

        var result = await _sut.GetUser(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetUser_NotFound_ReturnsNotFound()
    {
        _serviceMock.GetUserDetailAsync(999, Arg.Any<CancellationToken>())
            .Returns((UserDto?)null);

        var result = await _sut.GetUser(999, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetCurrentUser_Found_ReturnsOk()
    {
        _serviceMock.GetCurrentUserAsync(Arg.Any<CancellationToken>())
            .Returns(new UserDto { Id = 1, Username = "me" });

        var result = await _sut.GetCurrentUser(TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetCurrentUser_NotFound_ReturnsNotFound()
    {
        _serviceMock.GetCurrentUserAsync(Arg.Any<CancellationToken>())
            .Returns((UserDto?)null);

        var result = await _sut.GetCurrentUser(TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateUser_ReturnsCreated()
    {
        _serviceMock.CreateUserAsync(Arg.Any<CreateUserRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UserDto { Id = 1, Username = "new" });

        var result = await _sut.CreateUser(new CreateUserRequest { Username = "new", Email = "new@test.com" }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // R-10 (d): the holder of org.members.manage for organization 7 only; the service is never reached on a refusal.
    private UsersController OrganizationAdminOf7(bool rolesGiven = true)
    {
        var authz = Substitute.For<IResourceAuthorizationService>();
        authz.GetOrganizationsWithPermissionKeyAsync(Arg.Any<ClaimsPrincipal>(), PermissionKeys.OrganizationMembersManage, Arg.Any<CancellationToken>())
            .Returns(new List<int> { 7 });
        _serviceMock.CreateUserAsync(Arg.Any<CreateUserRequest>(), Arg.Any<CancellationToken>()).Returns(new UserDto { Id = 2, Username = "new" });
        var roles = Substitute.For<IOrganizationRoleAssignmentService>();
        roles.AreGivenByOrganizationAdministratorAsync(Arg.Any<IReadOnlyCollection<OrganizationRoleAssignmentDto>>(), Arg.Any<CancellationToken>()).Returns(rolesGiven);
        return new UsersController(_serviceMock, authz, roles)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "5")], "test")) }
            }
        };
    }

    private static CreateUserRequest In(int organizationId, List<string>? roles = null) => new()
    {
        Username = "new",
        Email = "new@test.com",
        Roles = roles ?? [],
        OrganizationRoles = [new OrganizationRoleAssignmentDto { OrganizationId = organizationId, Role = RoleNames.Reader }]
    };

    [Fact]
    public async Task CreateUser_OrganizationAdmin_InItsOrganization_ReturnsCreated()
    {
        var result = await OrganizationAdminOf7().CreateUser(In(7), TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task CreateUser_OrganizationAdmin_OtherOrganizationOrSiteRole_IsForbidden_AndNoOrganizationIsBadRequest()
    {
        var sut = OrganizationAdminOf7();
        var ct = TestContext.Current.CancellationToken;

        Assert.IsType<ForbidResult>((await sut.CreateUser(In(8), ct)).Result);
        Assert.IsType<ForbidResult>((await sut.CreateUser(In(7, [RoleNames.Admin]), ct)).Result);
        Assert.IsType<BadRequestObjectResult>((await sut.CreateUser(new CreateUserRequest { Username = "n", Email = "n@test.com" }, ct)).Result);
        // A role it does not give (Admin, a role of every organization or another organization's): refused too.
        Assert.IsType<ForbidResult>((await OrganizationAdminOf7(rolesGiven: false).CreateUser(In(7), ct)).Result);
        await _serviceMock.DidNotReceive().CreateUserAsync(Arg.Any<CreateUserRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateUser_Found_ReturnsOk()
    {
        _serviceMock.UpdateUserAsync(1, Arg.Any<UpdateUserRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UserDto { Id = 1, Username = "updated" });

        var result = await _sut.UpdateUser(1, new UpdateUserRequest { Username = "updated" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateUser_NotFound_ReturnsNotFound()
    {
        _serviceMock.UpdateUserAsync(999, Arg.Any<UpdateUserRequest>(), Arg.Any<CancellationToken>())
            .Returns((UserDto?)null);

        var result = await _sut.UpdateUser(999, new UpdateUserRequest { Username = "X" }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task DeleteUser_Deleted_ReturnsNoContent()
    {
        _serviceMock.DeleteUserAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteUser(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteUser_NotFound_ReturnsNotFound()
    {
        _serviceMock.DeleteUserAsync(999, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.DeleteUser(999, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ChangePassword_Success_ReturnsNoContent()
    {
        _serviceMock.ChangePasswordAsync(1, Arg.Any<ChangeUserPasswordRequest>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.ChangePassword(1, new ChangeUserPasswordRequest { NewPassword = "newpass123" }, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task ChangePassword_NotFound_ReturnsNotFound()
    {
        _serviceMock.ChangePasswordAsync(999, Arg.Any<ChangeUserPasswordRequest>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.ChangePassword(999, new ChangeUserPasswordRequest { NewPassword = "newpass123" }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ChangeOwnPassword_Success_ReturnsNoContent()
    {
        _serviceMock.ChangeOwnPasswordAsync(Arg.Any<ChangeUserPasswordRequest>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.ChangeOwnPassword(
            new ChangeUserPasswordRequest { CurrentPassword = "old", NewPassword = "newpass123" }, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task ChangeOwnPassword_Failure_ReturnsBadRequest()
    {
        _serviceMock.ChangeOwnPasswordAsync(Arg.Any<ChangeUserPasswordRequest>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.ChangeOwnPassword(
            new ChangeUserPasswordRequest { CurrentPassword = "wrong", NewPassword = "newpass123" }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task GetRoles_ReturnsOk()
    {
        _serviceMock.GetRolesAsync(Arg.Any<CancellationToken>())
            .Returns(["Admin", "User"]);

        var result = await _sut.GetRoles(TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var roles = Assert.IsType<List<string>>(ok.Value);
        Assert.Equal(2, roles.Count);
    }
}
