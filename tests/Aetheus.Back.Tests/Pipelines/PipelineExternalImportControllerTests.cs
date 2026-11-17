// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-11 security review, finding 1: importing an external pipeline creates a pipeline of the project, so it takes
/// exactly the checks of <c>POST /api/pipelines</c> (Pipeline WRITE in the global RBAC, the ADR-059 organization rule,
/// the ownership of the project), not only WRITE on the project. The service is a double: these tests exercise the
/// controller's authorization.
/// </summary>
public sealed class PipelineExternalImportControllerTests
{
    private const int ProjectId = 7;

    private readonly IPipelineExternalImportService _imports = Substitute.For<IPipelineExternalImportService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IPipelineOwnerAuthorization _owner = Substitute.For<IPipelineOwnerAuthorization>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ImportExternalPipelineRequest Request() => new()
    {
        RepositoryId = 3,
        FilePath = ".pipeline/oe1-ci.yaml",
        CommitSha = "0123456789abcdef0123456789abcdef01234567"
    };

    private PipelineExternalImportController Controller(string user) => new(_imports, _authz, _owner)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test"))
            }
        }
    };

    private void Grant(bool pipelineWrite, bool createInProject, bool own, bool projectWrite = true)
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, ProjectId, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(projectWrite);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, ProjectId, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(projectWrite);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, null, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(pipelineWrite);
        _authz.CanCreateInProjectAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, ProjectId, Arg.Any<CancellationToken>())
            .Returns(createInProject);
        _owner.CanOwnAsync(Arg.Any<ClaimsPrincipal>(), ProjectId, null, null, Arg.Any<CancellationToken>()).Returns(own);
    }

    [Fact]
    public async Task Import_UserWithProjectWriteOnly_IsForbiddenAndImportsNothing()
    {
        // Project WRITE (hence the project ownership) but no Pipeline WRITE in the global RBAC and no ADR-059 right.
        Grant(pipelineWrite: false, createInProject: false, own: true);

        var imported = await Controller("project-writer").Import(ProjectId, Request(), Ct);

        Assert.IsType<ForbidResult>(imported.Result);
        await _imports.DidNotReceiveWithAnyArgs().ImportAsync(default, default!, default!, Ct);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Import_MissingTheOrganizationRuleOrTheOwnership_IsForbidden(bool pipelineWrite, bool createInProject, bool own)
    {
        Grant(pipelineWrite, createInProject, own);

        var imported = await Controller("writer").Import(ProjectId, Request(), Ct);

        Assert.IsType<ForbidResult>(imported.Result);
        await _imports.DidNotReceiveWithAnyArgs().ImportAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task Import_WithEveryCheckOfAPipelineCreation_ImportsAsTheCaller()
    {
        Grant(pipelineWrite: true, createInProject: true, own: true);
        _imports.ImportAsync(ProjectId, Arg.Any<ImportExternalPipelineRequest>(), "writer", Arg.Any<CancellationToken>())
            .Returns(new PipelineDto { Id = 1 });

        var imported = await Controller("writer").Import(ProjectId, Request(), Ct);

        Assert.IsType<OkObjectResult>(imported.Result);
        await _imports.Received(1).ImportAsync(ProjectId, Arg.Any<ImportExternalPipelineRequest>(), "writer", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_WithoutProjectRead_IsForbiddenAndReadsNothing()
    {
        Grant(pipelineWrite: true, createInProject: true, own: true, projectWrite: false);

        var listed = await Controller("reader").List(ProjectId, Ct);

        Assert.IsType<ForbidResult>(listed.Result);
        await _imports.DidNotReceiveWithAnyArgs().ListAsync(default, Ct);
    }
}
