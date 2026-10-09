// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Projects;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R2-034: creating a project hands the caller's user id to the service, which makes them a
/// follower; an identity without a user row (the deployment identity) hands none.
/// </summary>
public sealed class ProjectsControllerCreatorTests
{
    private readonly IProjectService _service = Substitute.For<IProjectService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();

    public ProjectsControllerCreatorTests()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);
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
        await _service.Received(1).CreateProjectAsync(request, expected, TestContext.Current.CancellationToken);
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
