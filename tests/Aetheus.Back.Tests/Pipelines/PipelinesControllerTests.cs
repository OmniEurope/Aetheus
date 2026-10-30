// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class PipelinesControllerTests
{
    private readonly IPipelineService _service = Substitute.For<IPipelineService>();
    private readonly IPipelineRunService _runService = Substitute.For<IPipelineRunService>();
    private readonly IPipelineApprovalService _approvalService = Substitute.For<IPipelineApprovalService>();
    private readonly IPipelineArtifactService _artifactService = Substitute.For<IPipelineArtifactService>();
    private readonly IPipelineWebhookService _webhookService = Substitute.For<IPipelineWebhookService>();
    private readonly IPipelineRepository _pipelineRepo = Substitute.For<IPipelineRepository>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IPipelineRunToEndPolicy _runToEnd = Substitute.For<IPipelineRunToEndPolicy>();
    private readonly IPipelineFleetService _fleetService = Substitute.For<IPipelineFleetService>();
    private readonly PipelinesController _sut;
    private readonly PipelineFleetController _fleetSut;

    public PipelinesControllerTests()
    {
        // ADR-059: the caller's organization lets it give a pipeline to the project.
        _authz.CanCreateInProjectAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _authz.HasPermissionAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
            Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>())
            .Returns(true);
        // R-11: running and deciding approvals are named permissions of their own.
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _authz.GetUserOrganizationIdsAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns([1]);
        // ADR-059: validation runs in an organization where a role reads pipelines, not mere membership.
        _authz.GetOrganizationIdsWithPermissionAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, Permission.Read, Arg.Any<CancellationToken>())
            .Returns([1]);
        _runService.GetPipelineIdForRunAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);
        _approvalService.GetApprovalScopeAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PipelineApprovalScope(1, 3, null));
        _runService.IsServerAssignedToRunAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        // Default: YAML is valid. Tests that exercise the invalid-YAML path override this.
        _service.ValidateYamlStrict(Arg.Any<string>())
            .Returns(new YamlValidationResultDto { IsValid = true });
        _runService.PrepareRunAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Preparation(call.ArgAt<int>(0)));
        _runService.ResolveRunParametersAsync(
                Arg.Any<PipelineRunPreparation>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<PipelineRunPreparation>(0));
        _runService.GetRetryTargetServerIdsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<int>());
        _runService.PrepareRerunAsync(Arg.Any<int>(), Arg.Any<RerunMode>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRerunPlan(Preparation(1), null, null));
        _fleetService.GetItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => new PipelineFleetItemDto
            {
                PipelineId = call.ArgAt<int>(0),
                OrganizationId = 4,
                TemplateId = 7
            });
        // Every owner shares the project's organization unless a test says otherwise (RBAC audit, finding 6).
        _authz.BelongsToProjectOrganizationAsync(Arg.Any<int?>(), Arg.Any<ResourceType>(), Arg.Any<IEnumerable<int>?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        // The real owner authorization, not a substitute: these tests are about which owners a caller
        // may attach a pipeline to, and stubbing that away would assert on the stub instead.
        _sut = new PipelinesController(
            _service, _runService, _approvalService, _artifactService, _webhookService,
            new PipelineOwnerAuthorization(_service, _authz), _authz, _runToEnd);
        _fleetSut = new PipelineFleetController(_fleetService, _authz, Substitute.For<IPipelineTemplateService>());
        var controllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("ServerId", "1")], "test"))
            }
        };
        _sut.ControllerContext = controllerContext;
        _fleetSut.ControllerContext = controllerContext;
    }

    [Fact]
    public async Task GetFleet_BoundsResultsToAccessiblePipelinesAndOrganizations()
    {
        _authz.GetAccessibleResourceIdsAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, Permission.Read, Arg.Any<CancellationToken>())
            .Returns([1, 2]);
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns([4]);
        _fleetService.GetAsync(
                Arg.Any<PipelineFleetPaginationRequest>(),
                Arg.Any<IReadOnlyCollection<int>?>(),
                Arg.Any<IReadOnlyCollection<int>?>(),
                Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<PipelineFleetItemDto>());

        var response = await _fleetSut.GetFleet(new PipelineFleetPaginationRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(response.Result);
        await _fleetService.Received(1).GetAsync(
            Arg.Any<PipelineFleetPaginationRequest>(),
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 4 })),
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 1, 2 })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreviewFleetUpdate_TemplateReadDenied_DoesNotPreview()
    {
        _authz.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.PipelineTemplate, 7, Permission.Read,
                Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _fleetSut.PreviewFleetUpdate(
            1, new PipelineFleetUpdateRequest { TargetVersion = 2 }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _fleetService.DidNotReceive().PreviewUpdateAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyFleetUpdate_Authorized_ReturnsUpdatedPipeline()
    {
        _fleetService.UpdateAsync(1, Arg.Any<PipelineFleetUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineDto { Id = 1, Name = "updated" });

        var response = await _fleetSut.ApplyFleetUpdate(
            1, new PipelineFleetUpdateRequest { TargetVersion = 2 }, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal("updated", Assert.IsType<PipelineDto>(ok.Value).Name);
    }

    [Fact]
    public async Task ExtractTemplate_ForeignOrganization_DoesNotExtract()
    {
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns([9]);

        var response = await _fleetSut.ExtractTemplate(1, new ExtractPipelineTemplateRequest
        {
            TemplateName = "ci",
            Category = "CI"
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _fleetService.DidNotReceive().ExtractAsync(
            Arg.Any<int>(), Arg.Any<ExtractPipelineTemplateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteTemplate_ExactTemplateWriteDenied_DoesNotPromote()
    {
        _authz.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(), ResourceType.PipelineTemplate, 7, Permission.Write,
                Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _fleetSut.PromoteTemplate(1, new PromotePipelineTemplateRequest
        {
            ChangelogEntry = "change",
            YamlContent = "name: ci\nstages: []"
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _fleetService.DidNotReceive().PromoteAsync(
            Arg.Any<int>(), Arg.Any<PromotePipelineTemplateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreviewPromoteTemplate_Authorized_UsesExactTemplatePermission()
    {
        _fleetService.PreviewPromotionAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelinePromotePreviewDto { PipelineId = 1, TemplateId = 7 });

        var response = await _fleetSut.PreviewPromoteTemplate(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(response.Result);
        await _authz.Received(1).HasPermissionAsync(
            Arg.Any<ClaimsPrincipal>(), ResourceType.PipelineTemplate, 7, Permission.Write,
            Arg.Any<CancellationToken>());
    }

    private static PipelineRunPreparation Preparation(int pipelineId, IReadOnlyCollection<int>? targets = null)
        => new()
        {
            PipelineId = pipelineId,
            YamlSnapshot = "name: test\nstages: []",
            TargetServerIds = targets ?? []
        };

    [Fact]
    public async Task GetPipelines_ReturnsOk()
    {
        var result = new PaginatedResult<PipelineDto> { Items = [], TotalCount = 0 };
        _service.GetPipelinesAsync(Arg.Any<PipelinePaginationRequest>(), ct: TestContext.Current.CancellationToken).Returns(result);

        var response = await _sut.GetPipelines(new PipelinePaginationRequest(), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.IsType<PaginatedResult<PipelineDto>>(ok.Value);
    }

    [Fact]
    public async Task GetActiveRuns_AppliesReadablePipelineIdsAndProjectScope()
    {
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, Permission.Read, TestContext.Current.CancellationToken)
            .Returns([1, 3]);
        _runService.GetActiveRunsAsync(Arg.Any<List<int>?>(), 7, TestContext.Current.CancellationToken)
            .Returns([new PipelineRunDto { Id = 9, PipelineId = 3, Status = PipelineStatus.Running }]);

        var response = await _sut.GetActiveRuns(7, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Single(Assert.IsType<List<PipelineRunDto>>(ok.Value));
        await _runService.Received(1).GetActiveRunsAsync(
            Arg.Is<List<int>?>(ids => ids != null && ids.SequenceEqual(new[] { 1, 3 })), 7, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetRecentRuns_AppliesReadablePipelineIdsAndProjectScope()
    {
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, Permission.Read, TestContext.Current.CancellationToken)
            .Returns([1, 3]);
        _runService.GetRecentRunsAsync(Arg.Any<List<int>?>(), 7, null, TestContext.Current.CancellationToken)
            .Returns([new PipelineRunDto { Id = 9, PipelineId = 3, Status = PipelineStatus.Success }]);

        var response = await _sut.GetRecentRuns(7, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Single(Assert.IsType<List<PipelineRunDto>>(ok.Value));
        await _runService.Received(1).GetRecentRunsAsync(
            Arg.Is<List<int>?>(ids => ids != null && ids.SequenceEqual(new[] { 1, 3 })), 7, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetPipeline_Found_ReturnsOk()
    {
        var dto = new PipelineDto { Id = 1, Name = "CI" };
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken).Returns(dto);

        var response = await _sut.GetPipeline(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal("CI", ((PipelineDto)ok.Value!).Name);
    }

    [Fact]
    public async Task GetPipeline_NotFound_Returns404()
    {
        _service.GetPipelineAsync(99, TestContext.Current.CancellationToken).Returns((PipelineDto?)null);

        var response = await _sut.GetPipeline(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task CreatePipeline_ReturnsCreated()
    {
        var dto = new PipelineDto { Id = 5, Name = "New" };
        _service.CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), TestContext.Current.CancellationToken).Returns(dto);

        var response = await _sut.CreatePipeline(new CreatePipelineRequest { Name = "New", YamlDefinition = "yaml" }, TestContext.Current.CancellationToken);

        var created = Assert.IsType<CreatedAtActionResult>(response.Result);
        Assert.Equal(5, ((PipelineDto)created.Value!).Id);
    }

    [Fact]
    public async Task CreatePipeline_EnvironmentTheCallerCannotWrite_IsRefused()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 9,
            Permission.Write, Arg.Any<CancellationToken>()).Returns(false);

        var response = await _sut.CreatePipeline(
            new CreatePipelineRequest { Name = "New", YamlDefinition = "yaml", EnvironmentId = 9 },
            TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _service.DidNotReceive().CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePipeline_ProjectServerWhoseProjectTheCallerCannotWrite_IsRefused()
    {
        _service.GetProjectServerProjectIdAsync(7, Arg.Any<CancellationToken>()).Returns(42);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 42,
            Permission.Write, Arg.Any<CancellationToken>()).Returns(false);

        var response = await _sut.CreatePipeline(
            new CreatePipelineRequest { Name = "New", YamlDefinition = "yaml", ProjectServerId = 7 },
            TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _service.DidNotReceive().CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePipeline_ProjectServerThatCannotBeResolved_IsRefused()
    {
        // Fail closed: an owner nobody can name is not an owner nobody needs permission for.
        _service.GetProjectServerProjectIdAsync(7, Arg.Any<CancellationToken>()).Returns((int?)null);

        var response = await _sut.CreatePipeline(
            new CreatePipelineRequest { Name = "New", YamlDefinition = "yaml", ProjectServerId = 7 },
            TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _service.DidNotReceive().CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipeline_MovingIntoAnEnvironmentTheCallerCannotWrite_IsRefused()
    {
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken).Returns(new PipelineDto { Id = 1 });
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 9,
            Permission.Write, Arg.Any<CancellationToken>()).Returns(false);

        var response = await _sut.UpdatePipeline(
            1, new UpdatePipelineRequest { Name = "Updated", EnvironmentId = 9 },
            TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _service.DidNotReceive().UpdatePipelineAsync(
            Arg.Any<int>(), Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipeline_MovingOutOfAnEnvironmentTheCallerCannotWrite_IsRefused()
    {
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken)
            .Returns(new PipelineDto { Id = 1, EnvironmentId = 9 });
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Environment, 9,
            Permission.Write, Arg.Any<CancellationToken>()).Returns(false);

        var response = await _sut.UpdatePipeline(
            1, new UpdatePipelineRequest { Name = "Updated" }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _service.DidNotReceive().UpdatePipelineAsync(
            Arg.Any<int>(), Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePipeline_EnvironmentTheCallerCanWrite_IsAllowed()
    {
        _service.CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), TestContext.Current.CancellationToken)
            .Returns(new PipelineDto { Id = 5, Name = "New" });

        var response = await _sut.CreatePipeline(
            new CreatePipelineRequest { Name = "New", YamlDefinition = "yaml", EnvironmentId = 9 },
            TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(response.Result);
    }

    [Fact]
    public async Task CreatePipeline_ProjectWithAnEnvironmentOfAnotherOrganization_IsForbidden()
    {
        // RBAC audit, finding 6: writing both is not enough; the request validation refuses two owners
        // over HTTP, and the owner authorization holds the rule whatever the request shape.
        _authz.BelongsToProjectOrganizationAsync(3, ResourceType.Environment, Arg.Any<IEnumerable<int>?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _sut.CreatePipeline(
            new CreatePipelineRequest { Name = "New", YamlDefinition = "yaml", ProjectId = 3, EnvironmentId = 9 },
            TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _service.DidNotReceiveWithAnyArgs().CreatePipelineAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetRepositoryDefinition_NeedsWritePermission_ThenAnswersFoundOrNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 5, Permission.Write, ct).Returns(false);
        Assert.IsType<ForbidResult>((await _sut.GetRepositoryDefinition(5, "develop", null, ct)).Result);
        await _service.DidNotReceive().GetRepositoryDefinitionAsync(5, Arg.Any<string?>(), Arg.Any<string?>(), ct);

        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 5, Permission.Write, ct).Returns(true);
        _service.GetRepositoryDefinitionAsync(5, "develop", "deploy", ct)
            .Returns(new PipelineRepositoryDefinitionDto { Exists = true, Yaml = "name: deploy", Sha256 = "h" });
        var ok = Assert.IsType<OkObjectResult>((await _sut.GetRepositoryDefinition(5, "develop", "deploy", ct)).Result);
        Assert.Equal("h", Assert.IsType<PipelineRepositoryDefinitionDto>(ok.Value).Sha256);

        _service.GetRepositoryDefinitionAsync(404, null, null, ct).Returns((PipelineRepositoryDefinitionDto?)null);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Pipeline, 404, Permission.Write, ct).Returns(true);
        Assert.IsType<NotFoundResult>((await _sut.GetRepositoryDefinition(404, null, null, ct)).Result);
    }

    [Fact]
    public async Task UpdatePipeline_Found_ReturnsOk()
    {
        var dto = new PipelineDto { Id = 1, Name = "Updated" };
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken).Returns(new PipelineDto { Id = 1 });
        _service.UpdatePipelineAsync(1, Arg.Any<UpdatePipelineRequest>(), TestContext.Current.CancellationToken).Returns(dto);

        var response = await _sut.UpdatePipeline(1, new UpdatePipelineRequest { Name = "Updated" }, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal("Updated", ((PipelineDto)ok.Value!).Name);
    }

    [Fact]
    public async Task UpdatePipeline_NotFound_Returns404()
    {
        _service.UpdatePipelineAsync(99, Arg.Any<UpdatePipelineRequest>(), TestContext.Current.CancellationToken)
            .Returns((PipelineDto?)null);

        var response = await _sut.UpdatePipeline(99, new UpdatePipelineRequest { Name = "X" }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task CreatePipeline_InvalidYaml_ReturnsBadRequestAndDoesNotPersist()
    {
        _service.ValidateYamlStrict(Arg.Any<string>())
            .Returns(new YamlValidationResultDto { IsValid = false, Errors = ["bad stage"] });

        var response = await _sut.CreatePipeline(new CreatePipelineRequest { Name = "X", YamlDefinition = "::" }, TestContext.Current.CancellationToken);

        var bad = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("bad stage", ((YamlValidationResultDto)bad.Value!).Errors);
        await _service.DidNotReceive().CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipeline_InvalidYaml_ReturnsBadRequestAndDoesNotPersist()
    {
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken).Returns(new PipelineDto { Id = 1 });
        _service.ValidateYamlStrict(Arg.Any<string>())
            .Returns(new YamlValidationResultDto { IsValid = false, Errors = ["bad step"] });

        var response = await _sut.UpdatePipeline(1, new UpdatePipelineRequest { Name = "X", YamlDefinition = "::" }, TestContext.Current.CancellationToken);

        var bad = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("bad step", ((YamlValidationResultDto)bad.Value!).Errors);
        await _service.DidNotReceive().UpdatePipelineAsync(Arg.Any<int>(), Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreatePipeline_ProjectWriteDenied_DoesNotPersist()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 7, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _sut.CreatePipeline(new CreatePipelineRequest
        {
            Name = "release",
            ProjectId = 7,
            YamlDefinition = "name: release\nstages: []"
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _service.DidNotReceive().CreatePipelineAsync(Arg.Any<CreatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePipeline_ProjectMoveRequiresWriteOnBothProjects()
    {
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken).Returns(new PipelineDto { Id = 1, ProjectId = 7 });
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 8, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _sut.UpdatePipeline(1, new UpdatePipelineRequest
        {
            Name = "release",
            ProjectId = 8,
            YamlDefinition = "name: release\nstages: []"
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _authz.Received(1).HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 7, Permission.Write, Arg.Any<CancellationToken>());
        await _authz.Received(1).HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 8, Permission.Write, Arg.Any<CancellationToken>());
        await _service.DidNotReceive().UpdatePipelineAsync(Arg.Any<int>(), Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePipeline_Found_ReturnsNoContent()
    {
        _service.DeletePipelineAsync(1, TestContext.Current.CancellationToken).Returns(true);

        var response = await _sut.DeletePipeline(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(response);
    }

    [Fact]
    public async Task DeletePipeline_NotFound_Returns404()
    {
        _service.DeletePipelineAsync(99, TestContext.Current.CancellationToken).Returns(false);

        var response = await _sut.DeletePipeline(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response);
    }

    [Fact]
    public async Task TriggerRun_Found_ReturnsCreated()
    {
        var run = new PipelineRunDto { Id = 10, PipelineId = 1, Status = PipelineStatus.Running };
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken)
            .Returns(new PipelineDto { Id = 1, Name = "p", YamlDefinition = "name: p" });
        _service.ValidateYamlStrict(Arg.Any<string>())
            .Returns(new YamlValidationResultDto { IsValid = true });
        _runService.TriggerPreparedRunAsync(Arg.Any<PipelineRunPreparation>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>(), "request-42").Returns(run);

        var response = await _sut.TriggerRun(1, new PipelineRunRequest
        {
            IdempotencyKey = "request-42"
        }, TestContext.Current.CancellationToken);

        var created = Assert.IsType<CreatedAtActionResult>(response.Result);
        Assert.Equal(10, ((PipelineRunDto)created.Value!).Id);
        Assert.Equal(nameof(_sut.GetRun), created.ActionName);
        await _runService.Received(1).TriggerPreparedRunAsync(
            Arg.Any<PipelineRunPreparation>(), null, null,
            TestContext.Current.CancellationToken, "request-42");
    }

    [Fact]
    public async Task TriggerRun_PipelineNotFound_Returns404()
    {
        _service.GetPipelineAsync(99, TestContext.Current.CancellationToken).Returns((PipelineDto?)null);

        var response = await _sut.TriggerRun(99, null, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task TriggerRun_PipelineRemovedDuringPreparation_ReturnsBadRequestThatDoesNotBlameTheYaml()
    {
        // Preparation returns null only for a pipeline that no longer exists; a YAML that cannot be read
        // throws a BadRequestException carrying the parser's line and column instead (next test).
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken)
            .Returns(new PipelineDto { Id = 1, Name = "p", YamlDefinition = "name: p" });
        _runService.PrepareRunAsync(1, null, Arg.Any<CancellationToken>())
            .Returns((PipelineRunPreparation?)null);

        var response = await _sut.TriggerRun(1, null, TestContext.Current.CancellationToken);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        var message = Assert.IsType<string>(badRequest.Value);
        Assert.Contains("removed", message, StringComparison.Ordinal);
        Assert.DoesNotContain("YAML", message, StringComparison.Ordinal);
        await _runService.DidNotReceive().TriggerPreparedRunAsync(Arg.Any<PipelineRunPreparation>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }


    [Fact]
    public async Task Preflight_Found_ReturnsOk()
    {
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken)
            .Returns(new PipelineDto { Id = 1, Name = "p", YamlDefinition = "name: p" });
        _service.ValidateYamlStrict(Arg.Any<string>())
            .Returns(new YamlValidationResultDto { IsValid = true });
        _runService.PreflightAsync(Arg.Any<PipelineRunPreparation>(), null, TestContext.Current.CancellationToken)
            .Returns(new PipelinePreflightDto
            {
                Stages = [new PreflightStageDto { StageName = "build", Resolved = false, Reason = "no online server" }]
            });

        var response = await _sut.Preflight(1, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var dto = Assert.IsType<PipelinePreflightDto>(ok.Value);
        Assert.False(dto.Stages[0].Resolved);
    }

    [Fact]
    public async Task Preflight_PipelineNotFound_Returns404()
    {
        _service.GetPipelineAsync(99, TestContext.Current.CancellationToken).Returns((PipelineDto?)null);

        var response = await _sut.Preflight(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task Preflight_InvalidYaml_ReturnsBadRequest()
    {
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken)
            .Returns(new PipelineDto { Id = 1, Name = "p", YamlDefinition = "::bad::" });
        _runService.PrepareRunAsync(1, null, Arg.Any<CancellationToken>())
            .Returns((PipelineRunPreparation?)null);

        var response = await _sut.Preflight(1, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        await _runService.DidNotReceive().PreflightAsync(Arg.Any<PipelineRunPreparation>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRuns_ReturnsOk()
    {
        _runService.GetRunsAsync(1, Arg.Any<PipelineRunPaginationRequest>(), TestContext.Current.CancellationToken)
            .Returns(new PaginatedResult<PipelineRunDto>());

        var response = await _sut.GetRuns(1, new PipelineRunPaginationRequest(), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Empty(((PaginatedResult<PipelineRunDto>)ok.Value!).Items);
    }

    [Fact]
    public async Task GetRun_Found_ReturnsOk()
    {
        var run = new PipelineRunDto { Id = 10, PipelineId = 1 };
        _runService.GetRunAsync(10, TestContext.Current.CancellationToken).Returns(run);

        var response = await _sut.GetRun(10, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(10, ((PipelineRunDto)ok.Value!).Id);
    }

    [Fact]
    public async Task GetRun_NotFound_Returns404()
    {
        _runService.GetRunAsync(99, TestContext.Current.CancellationToken).Returns((PipelineRunDto?)null);

        var response = await _sut.GetRun(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    // --- ValidateYaml ---

    [Fact]
    public async Task ValidateYaml_Valid_ReturnsOk()
    {
        _service.ValidateYamlStrict(Arg.Any<string>())
            .Returns(new YamlValidationResultDto { IsValid = true });

        var response = await _sut.ValidateYaml(new ValidateYamlRequest { Yaml = "name: test" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(response.Result);
    }

    [Fact]
    public async Task ValidateYaml_Invalid_ReturnsBadRequest()
    {
        _service.ValidateYamlStrict(Arg.Any<string>())
            .Returns(new YamlValidationResultDto { IsValid = false, Errors = ["bad"] });

        var response = await _sut.ValidateYaml(new ValidateYamlRequest { Yaml = "bad yaml" }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(response.Result);
    }

    [Fact]
    public async Task ValidateYaml_ForeignOrganization_ReturnsForbidWithoutResolving()
    {
        var response = await _sut.ValidateYaml(new ValidateYamlRequest
        {
            Yaml = "name: test\nextends: secret@1\nstages: []",
            OrganizationId = 99
        }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _service.DidNotReceive().ValidateYamlStrictAsync(
            Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(),
            Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // --- DryRun ---

    [Fact]
    public async Task DryRun_Found_ReturnsOk()
    {
        var result = new DryRunResultDto();
        _runService.DryRunAsync(1, Arg.Any<Dictionary<string, string>?>(), TestContext.Current.CancellationToken).Returns(result);

        var response = await _sut.DryRun(1, null, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(response.Result);
    }

    [Fact]
    public async Task DryRun_NotFound_Returns404()
    {
        _runService.DryRunAsync(99, Arg.Any<Dictionary<string, string>?>(), TestContext.Current.CancellationToken)
            .Returns((DryRunResultDto?)null);

        var response = await _sut.DryRun(99, null, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    // --- CancelRun ---

    [Fact]
    public async Task CancelRun_Found_ReturnsNoContent()
    {
        _runService.CancelRunAsync(10, Arg.Any<PipelineRunCancellation>(), TestContext.Current.CancellationToken).Returns(true);

        var response = await _sut.CancelRun(10, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(response);
    }

    [Fact]
    public async Task R2103_CancelRun_NamesTheSignedInUserAsTheAuthor()
    {
        // Cancellation exposes its author.
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"))
            }
        };
        _runService.CancelRunAsync(10, Arg.Any<PipelineRunCancellation>(), TestContext.Current.CancellationToken).Returns(true);

        await _sut.CancelRun(10, TestContext.Current.CancellationToken);

        await _runService.Received(1).CancelRunAsync(
            10, Arg.Is<PipelineRunCancellation>(cancellation => cancellation.By == "alice"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancelRun_NotFound_Returns404()
    {
        _runService.CancelRunAsync(99, Arg.Any<PipelineRunCancellation>(), TestContext.Current.CancellationToken).Returns(false);

        var response = await _sut.CancelRun(99, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response);
    }

    // --- ResumeRun (R-39) ---

    [Fact]
    public async Task R39_ResumeRun_WithTheRunPermission_ResumesInTheSignedInUsersName()
    {
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"))
            }
        };

        var response = await _sut.ResumeRun(10, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(response);
        await _authz.Received(1).HasPermissionAsync(
            Arg.Any<ClaimsPrincipal>(), PermissionKeys.PipelineRun, 1, Arg.Any<CancellationToken>());
        await _runService.Received(1).ResumeAwaitingRunAsync(10, "alice", TestContext.Current.CancellationToken);
    }
    /// <summary>The same permission as cancelling the run: without pipeline.run
    /// on the run's pipeline, nothing is replayed.</summary>
    [Fact]
    public async Task R39_ResumeRun_WithoutTheRunPermission_IsForbiddenAndResumesNothing()
    {
        _authz.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(), PermissionKeys.PipelineRun, 1, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _sut.ResumeRun(10, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response);
        await _runService.DidNotReceive().ResumeAwaitingRunAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A resume replays the run's captured steps on its target servers, like a retry: without
    /// Server.Admin on every target of the run's captured definition, nothing is resumed.</summary>
    [Fact]
    public async Task R39_ResumeRun_CallerLacksServerAdminOnTarget_IsForbiddenAndResumesNothing()
    {
        _runService.GetRetryTargetServerIdsAsync(10, Arg.Any<CancellationToken>()).Returns(new List<int> { 42 });
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(),
                ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _sut.ResumeRun(10, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response);
        await _runService.DidNotReceive().ResumeAwaitingRunAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R39_ResumeRun_CallerAdministersEveryTarget_Resumes()
    {
        _runService.GetRetryTargetServerIdsAsync(10, Arg.Any<CancellationToken>()).Returns(new List<int> { 42, 43 });

        var response = await _sut.ResumeRun(10, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(response);
        await _authz.Received(1).HasPermissionAsync(
            Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>());
        await _authz.Received(1).HasPermissionAsync(
            Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 43, Permission.Admin, Arg.Any<CancellationToken>());
        await _runService.Received(1).ResumeAwaitingRunAsync(10, Arg.Any<string?>(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task R39_ResumeRun_RunDefinitionUnreadable_Returns404AndResumesNothing()
    {
        _runService.GetRetryTargetServerIdsAsync(10, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyCollection<int>?)null);

        var response = await _sut.ResumeRun(10, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response);
        await _runService.DidNotReceive().ResumeAwaitingRunAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R39_ResumeRun_UnknownRun_Returns404AndResumesNothing()
    {
        _runService.GetPipelineIdForRunAsync(77, Arg.Any<CancellationToken>()).Returns((int?)null);

        var response = await _sut.ResumeRun(77, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response);
        await _runService.DidNotReceive().ResumeAwaitingRunAsync(
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryFailedSteps_Found_ReturnsOk()
    {
        _runService.RetryFailedStepsAsync(10, TestContext.Current.CancellationToken)
            .Returns(new PipelineRunDto { Id = 10, PipelineId = 1, Status = PipelineStatus.Running });

        var response = await _sut.RetryFailedSteps(10, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(10, ((PipelineRunDto)ok.Value!).Id);
    }

    [Fact]
    public async Task RetryFailedSteps_ServiceReturnsNull_Returns404()
    {
        _runService.RetryFailedStepsAsync(10, TestContext.Current.CancellationToken).Returns((PipelineRunDto?)null);

        var response = await _sut.RetryFailedSteps(10, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task RetryFailedSteps_UnknownRun_Returns404AndDoesNotCallService()
    {
        _runService.GetPipelineIdForRunAsync(77, Arg.Any<CancellationToken>()).Returns((int?)null);

        var response = await _sut.RetryFailedSteps(77, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
        await _runService.DidNotReceive().RetryFailedStepsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // --- F-EXEC-1: pipeline trigger requires Server Admin on every resolvable target ---
    // A pipeline step is free-form shell (= RCE). Triggering is only Pipeline.Write-gated, so
    // without this gate a Pipeline.Write user could escalate to RCE on servers they do not
    // administer - the exact escalation the F-15 gate on POST /api/tasks prevents.

    [Fact]
    public async Task TriggerRun_CallerLacksServerAdminOnResolvedTarget_ReturnsForbidAndDoesNotTrigger()
    {
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken)
            .Returns(new PipelineDto { Id = 1, Name = "p", YamlDefinition = "name: p" });
        _runService.PrepareRunAsync(1, null, Arg.Any<CancellationToken>())
            .Returns(Preparation(1, [42]));
        // Pipeline.Write stays allowed (broad default); only Server.Admin on the target is denied.
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(),
                ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _sut.TriggerRun(1, null, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _runService.DidNotReceive().TriggerPreparedRunAsync(Arg.Any<PipelineRunPreparation>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerRun_SelectedBranchTargetsUnauthorizedServer_ReturnsForbidForPreparedSnapshot()
    {
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken)
            .Returns(new PipelineDto { Id = 1, Name = "p", YamlDefinition = "name: p" });
        _runService.PrepareRunAsync(1, "danger", Arg.Any<CancellationToken>())
            .Returns(Preparation(1, [99]) with { BranchName = "danger", CommitHash = new string('a', 40) });
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(),
                ResourceType.Server, 99, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _sut.TriggerRun(1,
            new PipelineRunRequest { SourceBranch = "danger" }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _runService.Received(1).PrepareRunAsync(1, "danger", Arg.Any<CancellationToken>());
        await _runService.DidNotReceive().TriggerPreparedRunAsync(Arg.Any<PipelineRunPreparation>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerRun_CallerHasServerAdminOnAllTargets_ReturnsCreated()
    {
        var run = new PipelineRunDto { Id = 10, PipelineId = 1, Status = PipelineStatus.Running };
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken)
            .Returns(new PipelineDto { Id = 1, Name = "p", YamlDefinition = "name: p" });
        _runService.PrepareRunAsync(1, null, Arg.Any<CancellationToken>())
            .Returns(Preparation(1, [42, 43]));
        // _authz default grants every permission, so the caller administers both targets.
        _runService.TriggerPreparedRunAsync(Arg.Any<PipelineRunPreparation>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>()).Returns(run);

        var response = await _sut.TriggerRun(1, null, TestContext.Current.CancellationToken);

        var created = Assert.IsType<CreatedAtActionResult>(response.Result);
        Assert.Equal(10, ((PipelineRunDto)created.Value!).Id);
    }

    [Fact]
    public async Task RetryFailedSteps_CallerLacksServerAdminOnTarget_ReturnsForbidAndDoesNotRetry()
    {
        _runService.GetPipelineIdForRunAsync(10, Arg.Any<CancellationToken>()).Returns(1);
        _runService.GetRetryTargetServerIdsAsync(10, Arg.Any<CancellationToken>()).Returns(new List<int> { 42 });
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(),
                ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _sut.RetryFailedSteps(10, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _runService.DidNotReceive().RetryFailedStepsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryFailedSteps_SnapshotTargetsServerTheCurrentDefinitionDoesNot_ReturnsForbid()
    {
        // BACK-020: the current definition targets 41, which the caller administers; the run being
        // retried captured a definition that targets 42. The retry replays the capture.
        _runService.PrepareRunAsync(1, null, Arg.Any<CancellationToken>()).Returns(Preparation(1, [41]));
        _runService.GetRetryTargetServerIdsAsync(10, Arg.Any<CancellationToken>()).Returns(new List<int> { 42 });
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(),
                ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _sut.RetryFailedSteps(10, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _runService.DidNotReceive().RetryFailedStepsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rerun_SnapshotTargetsServerTheCurrentDefinitionDoesNot_ReturnsForbidBeforeLaunch()
    {
        // BACK-020: same shape for a snapshot rerun, with the replayed parameters already applied.
        _runService.PrepareRunAsync(1, null, Arg.Any<CancellationToken>()).Returns(Preparation(1, [41]));
        _runService.PrepareRerunAsync(10, RerunMode.SnapshotSameCommit, Arg.Any<CancellationToken>())
            .Returns(new PipelineRerunPlan(Preparation(1, [42]), null, null));
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(),
                ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _sut.Rerun(10, RerunMode.SnapshotSameCommit, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _runService.DidNotReceive().TriggerPreparedRunAsync(Arg.Any<PipelineRunPreparation>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<Dictionary<string, string>?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>());
        await _runService.DidNotReceive().RerunAsync(Arg.Any<int>(), Arg.Any<RerunMode>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rerun_Authorized_LaunchesTheExactPlanItAuthorized()
    {
        var parameters = new Dictionary<string, string> { ["runner"] = "b" };
        var additional = new Dictionary<string, string> { ["X"] = "1" };
        var plan = new PipelineRerunPlan(Preparation(1, [42]), additional, parameters);
        _runService.PrepareRerunAsync(10, RerunMode.Current, Arg.Any<CancellationToken>()).Returns(plan);
        _runService.TriggerPreparedRunAsync(Arg.Any<PipelineRunPreparation>(),
                Arg.Any<Dictionary<string, string>?>(), Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new PipelineRunDto { Id = 11, PipelineId = 1 });

        var response = await _sut.Rerun(10, RerunMode.Current, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(response.Result);
        await _runService.Received(1).TriggerPreparedRunAsync(
            Arg.Is<PipelineRunPreparation>(prepared => ReferenceEquals(prepared, plan.Preparation)),
            additional, parameters, Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Preflight_CallerLacksServerAdminOnResolvedTarget_ReturnsForbidAndDoesNotPreflight()
    {
        _service.GetPipelineAsync(1, TestContext.Current.CancellationToken)
            .Returns(new PipelineDto { Id = 1, Name = "p", YamlDefinition = "name: p" });
        _runService.PrepareRunAsync(1, null, Arg.Any<CancellationToken>())
            .Returns(Preparation(1, [42]));
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(),
                ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _sut.Preflight(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        // The controller preflights the prepared run (the PipelineRunPreparation overload); the
        // id overload is checked too so neither path can run before the Server.Admin refusal.
        await _runService.DidNotReceive().PreflightAsync(
            Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
        await _runService.DidNotReceive().PreflightAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DryRun_CallerLacksServerAdminOnResolvedTarget_ReturnsForbidAndDoesNotDryRun()
    {
        _runService.PrepareRunAsync(1, null, Arg.Any<CancellationToken>())
            .Returns(Preparation(1, [42]));
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(),
                ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _sut.DryRun(1, null, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _runService.DidNotReceive().DryRunAsync(Arg.Any<int>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    // --- Artifacts ---

    [Fact]
    public async Task GetArtifacts_ReturnsOk()
    {
        _artifactService.GetArtifactsAsync(10, TestContext.Current.CancellationToken).Returns([]);

        var response = await _sut.GetArtifacts(10, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(response.Result);
    }

    [Fact]
    public async Task PublishArtifact_Found_ReturnsOk()
    {
        var artifact = new PipelineArtifactDto { Id = 1, Name = "art" };
        _artifactService.PublishArtifactAsync(10, Arg.Any<PublishArtifactRequest>(), TestContext.Current.CancellationToken)
            .Returns(artifact);

        var response = await _sut.PublishArtifact(10, new PublishArtifactRequest { Name = "art" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(response.Result);
    }

    [Fact]
    public async Task PublishArtifact_NotFound_Returns404()
    {
        _artifactService.PublishArtifactAsync(99, Arg.Any<PublishArtifactRequest>(), TestContext.Current.CancellationToken)
            .Returns((PipelineArtifactDto?)null);

        var response = await _sut.PublishArtifact(99, new PublishArtifactRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    // --- Approvals ---

    [Fact]
    public async Task GetApprovals_ReturnsOk()
    {
        _approvalService.GetApprovalsAsync(10, TestContext.Current.CancellationToken).Returns([]);

        var response = await _sut.GetApprovals(10, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(response.Result);
    }

    [Fact]
    public async Task DecideApproval_Found_ReturnsOk()
    {
        var approval = new PipelineApprovalDto { Id = 1 };
        _approvalService.DecideApprovalAsync(1, Arg.Any<ApprovalDecisionRequest>(), TestContext.Current.CancellationToken)
            .Returns(approval);

        var response = await _sut.DecideApproval(1, new ApprovalDecisionRequest { Decision = ApprovalStatus.Approved }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(response.Result);
    }

    [Theory]
    [InlineData(ApprovalStatus.TimedOut)]
    [InlineData(ApprovalStatus.Pending)]
    public async Task DecideApproval_ASystemOnlyStatus_IsRefusedBeforeAnything(ApprovalStatus decision)
    {
        // RBAC audit, finding 11: TimedOut belongs to the expiry sweep, never to a user.
        var response = await _sut.DecideApproval(1, new ApprovalDecisionRequest { Decision = decision }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        await _approvalService.DidNotReceiveWithAnyArgs().DecideApprovalAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DecideApproval_PipelineWriterWithoutProjectAdmin_IsForbidden()
    {
        // Writing the pipeline starts the run; it does not also let the run through its gate (finding 11).
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 3, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(false);

        var response = await _sut.DecideApproval(1, new ApprovalDecisionRequest { Decision = ApprovalStatus.Approved }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
        await _approvalService.DidNotReceiveWithAnyArgs().DecideApprovalAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DecideApproval_Refused_LetsItsReasonReachTheResponse()
    {
        // Refused decisions reach middleware as conflicts with an explanatory message.
        _approvalService.DecideApprovalAsync(99, Arg.Any<ApprovalDecisionRequest>(), TestContext.Current.CancellationToken)
            .Returns<Task<PipelineApprovalDto>>(_ => throw new ConflictException("Approval 99 was already Approved."));

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.DecideApproval(99, new ApprovalDecisionRequest { Decision = ApprovalStatus.Rejected }, TestContext.Current.CancellationToken));

        Assert.Equal("Approval 99 was already Approved.", refusal.Message);
    }

    // --- Test Results ---

    [Fact]
    public async Task GetTestResults_ReturnsOk()
    {
        _artifactService.GetTestResultsAsync(10, TestContext.Current.CancellationToken).Returns([]);

        var response = await _sut.GetTestResults(10, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(response.Result);
    }

    [Fact]
    public async Task PublishTestResults_Found_ReturnsOk()
    {
        var summary = new PipelineTestResultSummaryDto();
        _artifactService.PublishTestResultsAsync(10, Arg.Any<PublishTestResultsRequest>(), TestContext.Current.CancellationToken)
            .Returns(summary);

        var response = await _sut.PublishTestResults(10, new PublishTestResultsRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(response.Result);
    }

    [Fact]
    public async Task PublishTestResults_NotFound_Returns404()
    {
        _artifactService.PublishTestResultsAsync(99, Arg.Any<PublishTestResultsRequest>(), TestContext.Current.CancellationToken)
            .Returns((PipelineTestResultSummaryDto?)null);

        var response = await _sut.PublishTestResults(99, new PublishTestResultsRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    // --- Webhook ---

    [Fact]
    public async Task HandleWebhook_Valid_ReturnsOk()
    {
        _webhookService.HandleWebhookAsync(Arg.Any<string>(), Arg.Any<string?>(), TestContext.Current.CancellationToken)
            .Returns(true);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };
        _sut.ControllerContext.HttpContext.Request.Body = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}"));

        var response = await _sut.HandleWebhook(TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(response);
    }

    [Fact]
    public async Task HandleWebhook_Invalid_ReturnsUnauthorized()
    {
        _webhookService.HandleWebhookAsync(Arg.Any<string>(), Arg.Any<string?>(), TestContext.Current.CancellationToken)
            .Returns(false);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };
        _sut.ControllerContext.HttpContext.Request.Body = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}"));

        var response = await _sut.HandleWebhook(TestContext.Current.CancellationToken);

        Assert.IsType<UnauthorizedObjectResult>(response);
    }

    private void GivenRawCoverageBody(string xml, long? declaredLength = null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
        _sut.HttpContext.Request.Body = new MemoryStream(bytes);
        _sut.HttpContext.Request.ContentLength = declaredLength ?? bytes.Length;
    }

    [Fact]
    public async Task PublishCoverageRaw_ForwardsTheXmlWithItsStageAndStep()
    {
        GivenRawCoverageBody("<coverage line-rate=\"0.8\" />");
        var summary = new PipelineCoverageSummaryDto();
        _artifactService.PublishCoverageAsync(5, Arg.Is<PublishCoverageRequest>(request =>
                request.XmlContent == "<coverage line-rate=\"0.8\" />" && request.StageName == "Test" && request.StepName == "unit"),
                Arg.Any<CancellationToken>())
            .Returns(summary);

        var response = await _sut.PublishCoverageRaw(5, "Test", "unit", TestContext.Current.CancellationToken);

        Assert.Same(summary, Assert.IsType<OkObjectResult>(response.Result).Value);
    }

    [Fact]
    public async Task PublishCoverageRaw_ADeclaredBodyAboveTheLimit_IsRefusedUnread()
    {
        GivenRawCoverageBody("<coverage />", CoverageUploadLimits.MaxRawXmlBytes + 1);

        var response = await _sut.PublishCoverageRaw(5, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(413, Assert.IsType<StatusCodeResult>(response.Result).StatusCode);
    }

    [Theory]
    [InlineData("<coverage />", 201, 0)]
    [InlineData("<coverage />", 0, 201)]
    [InlineData("  ", 0, 0)]
    public async Task PublishCoverageRaw_OverlongNamesOrAnEmptyBody_AreBadRequests(string xml, int stageLength, int stepLength)
    {
        GivenRawCoverageBody(xml);

        var response = await _sut.PublishCoverageRaw(
            5, stageLength == 0 ? null : new string('s', stageLength), stepLength == 0 ? null : new string('t', stepLength),
            TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        await _artifactService.DidNotReceiveWithAnyArgs().PublishCoverageAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PublishCoverageRaw_FromAServerNotOnTheRun_IsForbidden()
    {
        GivenRawCoverageBody("<coverage />");
        _runService.IsServerAssignedToRunAsync(5, 1, Arg.Any<CancellationToken>()).Returns(false);

        var response = await _sut.PublishCoverageRaw(5, null, null, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(response.Result);
    }
}
