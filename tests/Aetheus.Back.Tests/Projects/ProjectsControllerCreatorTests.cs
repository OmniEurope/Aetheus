// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary> Creating a project hands the caller's user id to the service, which makes them a
/// follower; an identity without a user row (the deployment identity) hands none. </summary>
public sealed class ProjectsControllerCreatorTests
{
    private readonly IProjectService _service = Substitute.For<IProjectService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();

    public ProjectsControllerCreatorTests()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
        _authz.GetOrganizationIdsWithPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, Permission.Write, Arg.Any<CancellationToken>())
            .Returns([4]);
        _service.CreateProjectAsync(Arg.Any<CreateProjectRequest>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new ProjectDto { Id = 3, Name = "Shop" });
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("bootstrap", null)]
    public async Task CreateProject_PassesTheCallersUserId_OrNoneWithoutAUserRow(string nameIdentifier, int? expected)
    {
        var request = new CreateProjectRequest { Name = "Shop" };

        var result = await Controller(nameIdentifier).CreateProject(request, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        // ADR-059: without a named organization, the project goes to the caller's one writable organization.
        await _service.Received(1).CreateProjectAsync(
            Arg.Is<CreateProjectRequest>(sent => sent.Name == "Shop" && sent.OrganizationId == 4),
            expected, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateProject_InAnOrganizationThatDoesNotLetTheCallerCreate_IsForbidden()
    {
        _authz.CanCreateInOrganizationAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 9, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await Controller("7").CreateProject(
            new CreateProjectRequest { Name = "Shop", OrganizationId = 9 }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _service.DidNotReceive().CreateProjectAsync(
            Arg.Any<CreateProjectRequest>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateProject_WithSeveralWritableOrganizationsAndNoneNamed_IsABadRequest()
    {
        _authz.GetOrganizationIdsWithPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, Permission.Write, Arg.Any<CancellationToken>())
            .Returns([4, 5]);

        var result = await Controller("7").CreateProject(new CreateProjectRequest { Name = "Shop" }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    private ProjectsController Controller(string nameIdentifier) => new(_service, _authz, Substitute.For<IProjectDeletionService>())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, nameIdentifier)], "test"))
            }
        }
    };
}
