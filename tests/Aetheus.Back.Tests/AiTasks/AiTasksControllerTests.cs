// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.AiTasks;

public sealed class AiTasksControllerTests
{
    private readonly IAiTaskService _service = Substitute.For<IAiTaskService>();
    private readonly IAiRunnerProfileService _profiles = Substitute.For<IAiRunnerProfileService>();
    private readonly IResourceAuthorizationService _authorization =
        Substitute.For<IResourceAuthorizationService>();
    // The run-scoped RBAC lookup is an own-read on this module's repository now.
    // The run-scoped RBAC lookup goes through the service, like every other controller read.
    private readonly ITaskService _tasks = Substitute.For<ITaskService>();
    private readonly AiTasksController _controller;

    public AiTasksControllerTests()
    {
        _authorization.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<ResourceType>(),
                Arg.Any<int?>(),
                Arg.Any<Permission>(),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _authorization.GetAccessibleResourceIdsAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<ResourceType>(),
                Arg.Any<Permission>(),
                Arg.Any<CancellationToken>())
            .Returns([1, 2]);
        _controller = new AiTasksController(
            _service,
            _profiles,
            _authorization,
            _tasks)
        {
            ControllerContext = Context(
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "Admin"))
        };
    }

    [Fact]
    public async Task ProfileEndpoints_ReturnExpectedHttpResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var profile = new AiRunnerProfileDto { Id = 3, Name = "reviewer" };
        var page = new PaginatedResult<AiRunnerProfileDto>
        {
            Items = [profile],
            TotalCount = 1
        };
        _profiles.GetProfilesAsync(Arg.Any<PaginationRequest>(), ct).Returns(page);
        _profiles.GetProfileAsync(3, ct).Returns(profile);
        _profiles.GetProfileAsync(404, ct).Returns((AiRunnerProfileDto?)null);
        _profiles.CreateProfileAsync(Arg.Any<CreateAiRunnerProfileRequest>(), ct)
            .Returns(profile);
        _profiles.UpdateProfileAsync(3, Arg.Any<UpdateAiRunnerProfileRequest>(), ct)
            .Returns(profile);
        _profiles.UpdateProfileAsync(404, Arg.Any<UpdateAiRunnerProfileRequest>(), ct)
            .Returns((AiRunnerProfileDto?)null);
        _profiles.DeleteProfileAsync(3, ct).Returns(true);
        _profiles.DeleteProfileAsync(404, ct).Returns(false);

        Assert.IsType<OkObjectResult>(
            (await _controller.GetProfiles(new PaginationRequest(), ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetProfileOptions(ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetProfile(3, ct)).Result);
        Assert.IsType<NotFoundResult>((await _controller.GetProfile(404, ct)).Result);
        Assert.IsType<CreatedAtActionResult>(
            (await _controller.CreateProfile(new CreateAiRunnerProfileRequest(), ct)).Result);
        Assert.IsType<OkObjectResult>(
            (await _controller.UpdateProfile(3, new UpdateAiRunnerProfileRequest(), ct)).Result);
        Assert.IsType<NotFoundResult>(
            (await _controller.UpdateProfile(404, new UpdateAiRunnerProfileRequest(), ct)).Result);
        Assert.IsType<NoContentResult>(await _controller.DeleteProfile(3, ct));
        Assert.IsType<NotFoundResult>(await _controller.DeleteProfile(404, ct));
    }

    [Fact]
    public async Task TestProfile_ReturnsTheServiceOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new TestAiRunnerProfileRequest { Name = "p", Provider = AiProvider.Anthropic, Model = "m", ProfileId = 3 };
        _profiles.TestConnectionAsync(request, ct).Returns(new AiConnectionTestDto { Succeeded = true, Message = "ok" });

        var result = Assert.IsType<OkObjectResult>((await _controller.TestProfile(request, ct)).Result);

        Assert.True(Assert.IsType<AiConnectionTestDto>(result.Value).Succeeded);
    }

    [Fact]
    public async Task ProfileOptions_NonAdmin_UsesAuthorizedOrganizations()
    {
        var ct = TestContext.Current.CancellationToken;
        _controller.ControllerContext = Context(new Claim(ClaimTypes.Name, "member"));
        _authorization.GetUserOrganizationIdsAsync(
                Arg.Any<ClaimsPrincipal>(),
                ct)
            .Returns([7]);
        _profiles.GetProfileOptionsAsync(
                Arg.Is<List<int>>(ids => ids.SequenceEqual(new[] { 7 })),
                ct)
            .Returns([new AiRunnerProfileDto { Id = 3 }]);

        Assert.IsType<OkObjectResult>((await _controller.GetProfileOptions(ct)).Result);
        await _profiles.Received(1).GetProfileOptionsAsync(
            Arg.Is<List<int>>(ids => ids.SequenceEqual(new[] { 7 })),
            ct);
    }

    [Fact]
    public async Task DefinitionEndpoints_ExerciseAuthorizedCrudAndRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var definition = Definition();
        _service.GetDefinitionsAsync(
                Arg.Any<PaginationRequest>(),
                12,
                null,
                Arg.Any<List<int>?>(),
                Arg.Any<List<int>?>(),
                ct)
            .Returns(new PaginatedResult<AiTaskDefinitionDto> { Items = [definition] });
        _service.GetDefinitionAsync(11, ct).Returns(definition);
        _service.GetDefinitionAsync(404, ct).Returns((AiTaskDefinitionDto?)null);
        _service.CreateDefinitionAsync(Arg.Any<CreateAiTaskDefinitionRequest>(), ct)
            .Returns(definition);
        _service.UpdateDefinitionAsync(11, Arg.Any<UpdateAiTaskDefinitionRequest>(), ct)
            .Returns(definition);
        _service.DeleteDefinitionAsync(11, ct).Returns(true);
        _service.RunNowAsync(11, null, ct).Returns(new ServerTask { Id = 91 });

        Assert.IsType<OkObjectResult>(
            (await _controller.GetDefinitions(12, null, new PaginationRequest(), ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetDefinition(11, ct)).Result);
        Assert.IsType<NotFoundResult>((await _controller.GetDefinition(404, ct)).Result);
        Assert.IsType<CreatedAtActionResult>(
            (await _controller.CreateDefinition(
                new CreateAiTaskDefinitionRequest { ProjectId = 12 },
                ct)).Result);
        Assert.IsType<OkObjectResult>(
            (await _controller.UpdateDefinition(
                11,
                new UpdateAiTaskDefinitionRequest { ProjectId = 12 },
                ct)).Result);
        Assert.IsType<NoContentResult>(await _controller.DeleteDefinition(11, ct));
        var run = Assert.IsType<OkObjectResult>((await _controller.RunNow(11, ct)).Result);
        Assert.Equal(91, Assert.IsType<AiRunStartDto>(run.Value).TaskId);
    }

    [Fact]
    public async Task DefinitionMutations_ForbidBeforeCallingService()
    {
        var ct = TestContext.Current.CancellationToken;
        var definition = Definition();
        _service.GetDefinitionAsync(11, ct).Returns(definition);
        _authorization.HasPermissionAsync(
                Arg.Any<ClaimsPrincipal>(),
                ResourceType.Project,
                12,
                Arg.Any<Permission>(),
                ct)
            .Returns(false);

        Assert.IsType<ForbidResult>(
            (await _controller.CreateDefinition(
                new CreateAiTaskDefinitionRequest { ProjectId = 12 },
                ct)).Result);
        Assert.IsType<ForbidResult>(
            (await _controller.UpdateDefinition(
                11,
                new UpdateAiTaskDefinitionRequest { ProjectId = 12 },
                ct)).Result);
        Assert.IsType<ForbidResult>(await _controller.DeleteDefinition(11, ct));
        Assert.IsType<ForbidResult>((await _controller.RunNow(11, ct)).Result);
    }

    [Fact]
    public async Task ResultConsumptionAndAgentEndpoints_ReturnAuthorizedResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var definition = Definition();
        var result = new AiRunResultDto { Id = 88, ProjectId = 12 };
        var request = new PublishAiRunResultRequest
        {
            ServerTaskId = 91,
            ProfileName = "reviewer",
            ReportMarkdown = "report"
        };
        _service.GetDefinitionAsync(11, ct).Returns(definition);
        _service.GetResultsAsync(11, null, Arg.Any<PaginationRequest>(), ct)
            .Returns(new PaginatedResult<AiRunResultDto> { Items = [result] });
        _service.GetResultAsync(88, ct).Returns(result);
        _service.GetConsumptionAsync(12, ct).Returns(new AiConsumptionDto { RunCount = 3 });
        _tasks.GetTaskServerIdAsync(91, ct).Returns(9);
        _service.PublishResultAsync(request, ct).Returns(result);
        _controller.ControllerContext = Context(
            new Claim(ClaimTypes.Name, "agent"),
            new Claim("ServerId", "9"));

        Assert.IsType<OkObjectResult>(
            (await _controller.GetResults(11, null, new PaginationRequest(), ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.GetConsumption(12, ct)).Result);
        Assert.IsType<OkObjectResult>((await _controller.PublishResult(request, ct)).Result);
    }

    [Fact]
    public async Task ResultEndpoints_RejectMissingScopeAndWrongAgent()
    {
        var ct = TestContext.Current.CancellationToken;
        _controller.ControllerContext = Context(new Claim(ClaimTypes.Name, "member"));
        _service.GetDefinitionAsync(404, ct).Returns((AiTaskDefinitionDto?)null);
        _service.GetResultAsync(404, ct).Returns((AiRunResultDto?)null);
        _tasks.GetTaskServerIdAsync(91, ct).Returns(9);
        var request = new PublishAiRunResultRequest
        {
            ServerTaskId = 91,
            ProfileName = "reviewer",
            ReportMarkdown = "report"
        };

        Assert.IsType<NotFoundResult>(
            (await _controller.GetResults(404, null, new PaginationRequest(), ct)).Result);
        Assert.IsType<ForbidResult>(
            (await _controller.GetResults(null, null, new PaginationRequest(), ct)).Result);
        Assert.IsType<ForbidResult>((await _controller.GetConsumption(null, ct)).Result);
        Assert.IsType<ForbidResult>((await _controller.PublishResult(request, ct)).Result);
    }

    private static AiTaskDefinitionDto Definition() => new()
    {
        Id = 11,
        Name = "Review",
        ProfileId = 3,
        ProjectId = 12
    };

    [Fact]
    public async Task R3_7_ReadTheModelListNow_IsOk_NotFound_OrTooManyRequestsWithRetryAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        var profile = new AiRunnerProfileDto { Id = 3, Name = "reviewer", AvailableModels = ["gpt-5.5"] };
        _profiles.RefreshModelListAsync(3, ct).Returns(new AiModelListRefreshAttempt(profile, null));
        _profiles.RefreshModelListAsync(404, ct).Returns((AiModelListRefreshAttempt?)null);
        _profiles.RefreshModelListAsync(5, ct).Returns(new AiModelListRefreshAttempt(profile, TimeSpan.FromSeconds(41.2)));

        var ok = Assert.IsType<OkObjectResult>((await _controller.RefreshModelList(3, ct)).Result);
        Assert.Same(profile, ok.Value);
        Assert.IsType<NotFoundResult>((await _controller.RefreshModelList(404, ct)).Result);
        var tooSoon = Assert.IsType<ObjectResult>((await _controller.RefreshModelList(5, ct)).Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, tooSoon.StatusCode);
        Assert.Contains("42 s", Assert.IsType<ApiError>(tooSoon.Value).Message, StringComparison.Ordinal);
        Assert.Equal("42", _controller.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public void R3_7_ReadTheModelListNow_IsAdminOnly_LikeEditingTheProfile()
    {
        static string? Roles(string name) => typeof(AiTasksController).GetMethod(name)!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single().Roles;

        Assert.Equal(Roles(nameof(AiTasksController.UpdateProfile)), Roles(nameof(AiTasksController.RefreshModelList)));
        Assert.Equal("Admin", Roles(nameof(AiTasksController.RefreshModelList)));
    }

    private static ControllerContext Context(params Claim[] claims) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
        }
    };
}
