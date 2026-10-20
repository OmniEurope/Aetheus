// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.ExternalRepos;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class ExternalReposControllerTests
{
    private readonly IExternalRepoService _service = Substitute.For<IExternalRepoService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly ExternalReposController _sut;

    public ExternalReposControllerTests()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = new ExternalReposController(_service, _authz);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
            }
        };
    }

    [Fact]
    public async Task Attach_Enabled_ReturnsOk()
    {
        _service.AttachAsync(Arg.Any<AttachExternalRepoRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExternalRepoDto { ProjectId = 1, OwnerOrGroup = "acme", RepositoryName = "demo" });

        var result = await _sut.Attach(new AttachExternalRepoRequest { ProjectId = 1, OwnerOrGroup = "acme", RepositoryName = "demo" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetForProject_WithoutExternalRepository_AnswersNoContent()
    {
        // The normal "nothing attached" state is 204, never a 404 logged as a warning.
        _service.GetForProjectAsync(2, Arg.Any<CancellationToken>()).Returns((ExternalRepoDto?)null);

        var result = await _sut.GetForProject(2, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result.Result);
    }

    [Fact]
    public async Task GetForProject_WithExternalRepository_AnswersIt()
    {
        var repository = new ExternalRepoDto { ProjectId = 2, OwnerOrGroup = "acme", RepositoryName = "demo" };
        _service.GetForProjectAsync(2, Arg.Any<CancellationToken>()).Returns(repository);

        var result = await _sut.GetForProject(2, TestContext.Current.CancellationToken);

        Assert.Same(repository, Assert.IsType<OkObjectResult>(result.Result).Value);
    }
}
