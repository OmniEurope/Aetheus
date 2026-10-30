// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Organizations;
using Aetheus.Shared.Components.Organizations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class OrganizationsControllerTests
{
    private readonly IOrganizationService _serviceMock = Substitute.For<IOrganizationService>();
    private readonly Aetheus.Back.Services.IResourceAuthorizationService _authz =
        Substitute.For<Aetheus.Back.Services.IResourceAuthorizationService>();
    private readonly OrganizationsController _sut;

    public OrganizationsControllerTests()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), PermissionKeys.OrganizationMembersManage, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = new OrganizationsController(_serviceMock, _authz);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Admin")], "test"))
            }
        };
    }

    private void ActAsOrganizationAdmin() =>
        _sut.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "org-admin")], "test"));

    // S2: an Organization Admin cannot walk the global user ids.
    [Fact]
    public async Task AddMember_OrganizationAdminById_IsRefused()
    {
        ActAsOrganizationAdmin();

        var result = await _sut.AddMember(1,
            new AddOrganizationMemberRequest { UserId = 5, Role = OrganizationRole.Member }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        await _serviceMock.DidNotReceive().AddMemberAsync(
            Arg.Any<int>(), Arg.Any<AddOrganizationMemberRequest>(), Arg.Any<CancellationToken>());
    }

    // S2: by exact login it works, and the found user's e-mail is not read back.
    [Fact]
    public async Task AddMember_OrganizationAdminByLogin_HidesTheEmail()
    {
        ActAsOrganizationAdmin();
        _serviceMock.AddMemberAsync(1, Arg.Any<AddOrganizationMemberRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OrganizationMemberDto(10, 5, "bob", "b@c.d", OrganizationRole.Member, DateTime.UtcNow));

        var result = await _sut.AddMember(1,
            new AddOrganizationMemberRequest { UserLogin = "bob", Role = OrganizationRole.Member }, TestContext.Current.CancellationToken);

        var member = Assert.IsType<OrganizationMemberDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Null(member.Email);
    }

    [Fact]
    public async Task GetOrganizations_ReturnsOk()
    {
        // The site Admin administers every organization (null): the list is not limited.
        _authz.GetOrganizationsWithPermissionKeyAsync(Arg.Any<ClaimsPrincipal>(), PermissionKeys.OrganizationMembersManage, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<int>?)null);
        _serviceMock.GetOrganizationsAsync(null, Arg.Any<PaginationRequest>(), null, Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<OrganizationDto>
            {
                Items = [new OrganizationDto(1, "Acme", "acme", "", 2, 3, DateTime.UtcNow, DateTime.UtcNow)],
                TotalCount = 1
            });

        var result = await _sut.GetOrganizations(null, new PaginationRequest(), null, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetMyOrganizations_ReturnsOk()
    {
        _serviceMock.GetMyOrganizationsAsync("admin", Arg.Any<CancellationToken>())
            .Returns([new MyOrganizationDto(1, "Acme", "acme", OrganizationRole.Owner)]);

        var result = await _sut.GetMyOrganizations(TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetMyOrganizations_NoUsername_ReturnsUnauthorized()
    {
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };

        var result = await _sut.GetMyOrganizations(TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public async Task GetOrganization_Found_ReturnsOk()
    {
        _serviceMock.GetOrganizationAsync(1, Arg.Any<CancellationToken>())
            .Returns(new OrganizationDetailDto(1, "Acme", "acme", "", DateTime.UtcNow, DateTime.UtcNow, [], []));

        var result = await _sut.GetOrganization(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetOrganization_NotFound_ReturnsNotFound()
    {
        _serviceMock.GetOrganizationAsync(99, Arg.Any<CancellationToken>())
            .Returns((OrganizationDetailDto?)null);

        var result = await _sut.GetOrganization(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateOrganization_ReturnsCreated()
    {
        _serviceMock.CreateOrganizationAsync(Arg.Any<CreateOrganizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OrganizationDto(1, "New", "new", "", 0, 0, DateTime.UtcNow, DateTime.UtcNow));

        var result = await _sut.CreateOrganization(
            new CreateOrganizationRequest { Name = "New", Slug = "new" }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task UpdateOrganization_Found_ReturnsOk()
    {
        _serviceMock.UpdateOrganizationAsync(1, Arg.Any<UpdateOrganizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OrganizationDto(1, "Up", "up", "", 0, 0, DateTime.UtcNow, DateTime.UtcNow));

        var result = await _sut.UpdateOrganization(1,
            new UpdateOrganizationRequest { Name = "Up", Slug = "up" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateOrganization_NotFound_ReturnsNotFound()
    {
        _serviceMock.UpdateOrganizationAsync(99, Arg.Any<UpdateOrganizationRequest>(), Arg.Any<CancellationToken>())
            .Returns((OrganizationDto?)null);

        var result = await _sut.UpdateOrganization(99,
            new UpdateOrganizationRequest { Name = "X", Slug = "x" }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task DeleteOrganization_Found_ReturnsNoContent()
    {
        _serviceMock.DeleteOrganizationAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteOrganization(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteOrganization_NotFound_ReturnsNotFound()
    {
        _serviceMock.DeleteOrganizationAsync(99, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.DeleteOrganization(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AddMember_ReturnsOk()
    {
        _serviceMock.AddMemberAsync(1, Arg.Any<AddOrganizationMemberRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OrganizationMemberDto(10, 5, "bob", "b@c.d", OrganizationRole.Member, DateTime.UtcNow));

        var result = await _sut.AddMember(1,
            new AddOrganizationMemberRequest { UserId = 5, Role = OrganizationRole.Member }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // R-10: only a site-wide Admin or a holder of org.members.manage for the organization (R-11) manages its members.
    [Fact]
    public async Task AddMember_NotOrganizationAdmin_IsForbidden()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), PermissionKeys.OrganizationMembersManage, 1, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.AddMember(1,
            new AddOrganizationMemberRequest { UserId = 5, Role = OrganizationRole.Member }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceive().AddMemberAsync(
            Arg.Any<int>(), Arg.Any<AddOrganizationMemberRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateMember_Found_ReturnsOk()
    {
        _serviceMock.UpdateMemberAsync(1, 10, Arg.Any<UpdateOrganizationMemberRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OrganizationMemberDto(10, 5, "bob", null, OrganizationRole.Owner, DateTime.UtcNow));

        var result = await _sut.UpdateMember(1, 10,
            new UpdateOrganizationMemberRequest { Role = OrganizationRole.Owner }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateMember_NotFound_ReturnsNotFound()
    {
        _serviceMock.UpdateMemberAsync(1, 99, Arg.Any<UpdateOrganizationMemberRequest>(), Arg.Any<CancellationToken>())
            .Returns((OrganizationMemberDto?)null);

        var result = await _sut.UpdateMember(1, 99,
            new UpdateOrganizationMemberRequest { Role = OrganizationRole.Owner }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task RemoveMember_Found_ReturnsNoContent()
    {
        _serviceMock.RemoveMemberAsync(1, 10, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.RemoveMember(1, 10, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task RemoveMember_NotFound_ReturnsNotFound()
    {
        _serviceMock.RemoveMemberAsync(1, 99, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.RemoveMember(1, 99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AssignProjects_ReturnsNoContent()
    {
        _serviceMock.AssignProjectsAsync(1, Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.AssignProjects(1, new AssignProjectsRequest { ProjectIds = [10, 20] }, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }
}
