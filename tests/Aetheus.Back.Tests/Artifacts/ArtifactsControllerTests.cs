// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using System.Text;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Artifacts;

public class ArtifactsControllerTests
{
    private readonly IArtifactService _service = Substitute.For<IArtifactService>();
    private readonly IChunkedArtifactUploadService _chunkedUploads = Substitute.For<IChunkedArtifactUploadService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly ArtifactsController _sut;

    public ArtifactsControllerTests()
    {
        _sut = new ArtifactsController(_service, _chunkedUploads, _authz);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
            }
        };
    }

    private void Allow(bool value)
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int>(),
            Arg.Any<Permission>(), Arg.Any<CancellationToken>()).Returns(value);
        // Authorization runs against the TRANSITIVELY resolved owner, so allowing a permission also
        // means naming the project it is held on. Without this the owner is unresolvable and every
        // call refuses - which is the fail-closed behaviour, exercised on its own below.
        _service.GetOwningProjectIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);
    }

    private static PipelineArtifactDto Artifact(int id = 1, int? projectId = 1) =>
        new() { Id = id, Name = "build", ProjectId = projectId };

    // --- GetProjectArtifacts ---

    [Fact]
    public async Task GetProjectArtifacts_Authorized_ReturnsOk()
    {
        Allow(true);
        _service.GetProjectArtifactsAsync(5, Arg.Any<ProjectArtifactsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<PipelineArtifactDto> { Items = [Artifact()], TotalCount = 1 });

        var result = await _sut.GetProjectArtifacts(5, new ProjectArtifactsRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetProjectArtifacts_Forbidden_ReturnsForbid()
    {
        Allow(false);

        var result = await _sut.GetProjectArtifacts(5, new ProjectArtifactsRequest(), TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // --- GetArtifact ---

    [Fact]
    public async Task GetArtifact_NotFound_ReturnsNotFound()
    {
        _service.GetArtifactAsync(9, Arg.Any<CancellationToken>()).Returns((PipelineArtifactDto?)null);

        var result = await _sut.GetArtifact(9, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>Resolves artifact ownership through its pipeline's environment or project-server
    /// relationship. An unresolved owner must deny access rather than bypass
    /// authorization.</summary>
    [Fact]
    public async Task GetArtifact_ArtifactColumnHasNoProject_StillAuthorizesAgainstTheResolvedOwner()
    {
        _service.GetArtifactAsync(1, Arg.Any<CancellationToken>()).Returns(Artifact(projectId: null));
        // The pipeline is owned through an environment, which resolves to project 4.
        _service.GetOwningProjectIdAsync(1, Arg.Any<CancellationToken>()).Returns(4);
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 4,
            Permission.Read, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.GetArtifact(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _authz.Received(1).HasPermissionAsync(
            Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 4, Permission.Read, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetArtifact_OwnerCannotBeResolved_ReturnsForbidRatherThanOk()
    {
        _service.GetArtifactAsync(1, Arg.Any<CancellationToken>()).Returns(Artifact(projectId: null));
        _service.GetOwningProjectIdAsync(1, Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _sut.GetArtifact(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task DownloadArtifact_OwnerCannotBeResolved_RefusesBeforeOpeningTheStream()
    {
        _service.GetArtifactAsync(1, Arg.Any<CancellationToken>()).Returns(Artifact(projectId: null));
        _service.GetOwningProjectIdAsync(1, Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _sut.DownloadArtifact(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
        await _service.DidNotReceive().DownloadArtifactAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PromoteArtifact_OwnerCannotBeResolved_RefusesBeforePromoting()
    {
        _service.GetArtifactAsync(1, Arg.Any<CancellationToken>()).Returns(Artifact(projectId: null));
        _service.GetOwningProjectIdAsync(1, Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _sut.PromoteArtifact(
            1, new PromoteArtifactRequest { EnvironmentName = "prod" }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _service.DidNotReceive().PromoteToEnvironmentAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetArtifact_ProjectNoPermission_ReturnsForbid()
    {
        _service.GetArtifactAsync(1, Arg.Any<CancellationToken>()).Returns(Artifact());
        Allow(false);

        var result = await _sut.GetArtifact(1, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetArtifact_ProjectWithPermission_ReturnsOk()
    {
        _service.GetArtifactAsync(1, Arg.Any<CancellationToken>()).Returns(Artifact());
        Allow(true);

        var result = await _sut.GetArtifact(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // --- DownloadArtifact ---

    [Fact]
    public async Task DownloadArtifact_NotFound_ReturnsNotFound()
    {
        _service.GetArtifactAsync(9, Arg.Any<CancellationToken>()).Returns((PipelineArtifactDto?)null);

        var result = await _sut.DownloadArtifact(9, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DownloadArtifact_StreamNull_ReturnsNotFound()
    {
        _service.GetArtifactAsync(1, Arg.Any<CancellationToken>()).Returns(Artifact());
        Allow(true);
        _service.DownloadArtifactAsync(1, Arg.Any<CancellationToken>()).Returns((Stream?)null);

        var result = await _sut.DownloadArtifact(1, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DownloadArtifact_Success_ReturnsFile()
    {
        _service.GetArtifactAsync(1, Arg.Any<CancellationToken>()).Returns(Artifact());
        Allow(true);
        _service.DownloadArtifactAsync(1, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(Encoding.UTF8.GetBytes("zip-bytes")));

        var result = await _sut.DownloadArtifact(1, TestContext.Current.CancellationToken);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/zip", file.ContentType);
        Assert.Equal("build.zip", file.FileDownloadName);
    }

    // --- PromoteArtifact ---

    [Fact]
    public async Task PromoteArtifact_NotFound_ReturnsNotFound()
    {
        _service.GetArtifactAsync(9, Arg.Any<CancellationToken>()).Returns((PipelineArtifactDto?)null);

        var result = await _sut.PromoteArtifact(9, new PromoteArtifactRequest { EnvironmentName = "prod" }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task PromoteArtifact_EmptyEnvironment_ReturnsBadRequest()
    {
        _service.GetArtifactAsync(1, Arg.Any<CancellationToken>()).Returns(Artifact());
        Allow(true);

        var result = await _sut.PromoteArtifact(1, new PromoteArtifactRequest { EnvironmentName = "" }, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task PromoteArtifact_Success_ReturnsOk()
    {
        _service.GetArtifactAsync(1, Arg.Any<CancellationToken>()).Returns(Artifact());
        Allow(true);
        _service.PromoteToEnvironmentAsync(1, "prod", Arg.Any<CancellationToken>())
            .Returns(Artifact());

        var result = await _sut.PromoteArtifact(1, new PromoteArtifactRequest { EnvironmentName = "prod" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    // --- UploadArtifact ---

    [Fact]
    public async Task UploadArtifact_MissingServerIdClaim_ReturnsForbid()
    {
        var result = await _sut.UploadArtifact(1, "art", null, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UploadArtifact_NotAssignedToRun_ReturnsForbid()
    {
        SetAgentUser(serverId: 7);
        _service.IsAgentAssignedToRunAsync(1, 7, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.UploadArtifact(1, "art", null, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UploadArtifact_InvalidName_ReturnsBadRequest()
    {
        SetAgentUser(serverId: 7);
        _service.IsAgentAssignedToRunAsync(1, 7, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.UploadArtifact(1, "   ", null, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UploadArtifact_Success_ReturnsOk()
    {
        SetAgentUser(serverId: 7);
        _service.IsAgentAssignedToRunAsync(1, 7, Arg.Any<CancellationToken>()).Returns(true);
        _service.PublishArtifactAsync(1, "art", null, 7, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Artifact());

        var result = await _sut.UploadArtifact(1, "art", null, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UploadArtifact_MissingContentLength_ReturnsLengthRequired()
    {
        SetAgentUser(serverId: 7);
        _service.IsAgentAssignedToRunAsync(1, 7, Arg.Any<CancellationToken>()).Returns(true);
        _sut.ControllerContext.HttpContext.Request.ContentLength = null;

        var result = await _sut.UploadArtifact(1, "art", null, TestContext.Current.CancellationToken);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status411LengthRequired, status.StatusCode);
        await _service.DidNotReceive().PublishArtifactAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<long>(),
            Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    // --- Chunked upload ---

    [Fact]
    public async Task BeginChunkedUpload_MissingServerIdClaim_ReturnsForbidWithoutOpeningASession()
    {
        var result = await _sut.BeginChunkedUpload(1, "art", null, 2, Digest, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _chunkedUploads.DidNotReceiveWithAnyArgs().BeginAsync(default, default!, default, default, default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("   ", null)]
    [InlineData("art", "stage")]
    public async Task BeginChunkedUpload_InvalidNameOrStage_ReturnsBadRequest(string name, string? stage)
    {
        AssignAgent();
        var stageName = stage?.PadRight(201, 'x');

        var result = await _sut.BeginChunkedUpload(1, name, stageName, 2, Digest, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        await _chunkedUploads.DidNotReceiveWithAnyArgs().BeginAsync(default, default!, default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BeginChunkedUpload_AssignedAgent_ReturnsTheSessionOpenedForThatRun()
    {
        AssignAgent();
        var session = new ChunkedUploadSession(new string('a', 32), 1, "art", "build", 3, Digest, DateTime.UnixEpoch);
        _chunkedUploads.BeginAsync(1, "art", "build", 3, Digest, Arg.Any<CancellationToken>()).Returns(session);

        var result = await _sut.BeginChunkedUpload(1, "art", "build", 3, Digest, TestContext.Current.CancellationToken);

        Assert.Same(session, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task UploadChunkedPart_RefusedPart_ReturnsBadRequestNamingTheReason()
    {
        AssignAgent();
        var refused = new ChunkedPartResult(false, 1, "The part digest does not match its content.");
        _chunkedUploads.AcceptPartAsync(1, "u", 2, Digest, Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(refused);

        var result = await _sut.UploadChunkedPart(1, "u", 2, Digest, TestContext.Current.CancellationToken);

        Assert.Same(refused, Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task UploadChunkedPart_AcceptedPart_ReturnsOk()
    {
        AssignAgent();
        var accepted = new ChunkedPartResult(true, 2);
        _chunkedUploads.AcceptPartAsync(1, "u", 1, Digest, Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(accepted);

        var result = await _sut.UploadChunkedPart(1, "u", 1, Digest, TestContext.Current.CancellationToken);

        Assert.Same(accepted, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task UploadChunkedPart_AgentNotOnTheRun_ReturnsForbid()
    {
        SetAgentUser(serverId: 7);
        _service.IsAgentAssignedToRunAsync(1, 7, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.UploadChunkedPart(1, "u", 0, Digest, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("art", "stage")]
    public async Task CompleteChunkedUpload_InvalidNameOrStage_ReturnsBadRequestWithoutAssembling(string name, string? stage)
    {
        AssignAgent();
        var stageName = stage?.PadRight(201, 'x');

        var result = await _sut.CompleteChunkedUpload(1, "u", name, stageName, TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        await _chunkedUploads.DidNotReceiveWithAnyArgs().CompleteAsync<PipelineArtifactDto?>(
            default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CompleteChunkedUpload_PublishesTheVerifiedFileUnderTheRequestedName()
    {
        AssignAgent();
        var verified = new VerifiedArtifactFile("assembled", 7, Digest);
        _chunkedUploads.CompleteAsync(1, "u", Arg.Any<Func<VerifiedArtifactFile, Task<PipelineArtifactDto?>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<VerifiedArtifactFile, Task<PipelineArtifactDto?>>>()(verified));
        _service.PublishVerifiedArtifactAsync(1, "art", "build", verified, Arg.Any<CancellationToken>())
            .Returns(Artifact(id: 9));

        var result = await _sut.CompleteChunkedUpload(1, "u", "art", "build", TestContext.Current.CancellationToken);

        Assert.Equal(9, Assert.IsType<PipelineArtifactDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Id);
    }

    [Fact]
    public async Task CompleteChunkedUpload_RunUnknownToThePublisher_ReturnsNotFound()
    {
        AssignAgent();
        _chunkedUploads.CompleteAsync(1, "u", Arg.Any<Func<VerifiedArtifactFile, Task<PipelineArtifactDto?>>>(),
                Arg.Any<CancellationToken>())
            .Returns((PipelineArtifactDto?)null);

        var result = await _sut.CompleteChunkedUpload(1, "u", "art", null, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Theory]
    [InlineData(true, typeof(NoContentResult))]
    [InlineData(false, typeof(NotFoundResult))]
    public async Task AbandonChunkedUpload_ReportsWhetherTheRunOwnedTheUpload(bool discarded, Type expected)
    {
        AssignAgent();
        _chunkedUploads.DiscardForRunAsync(1, "u", Arg.Any<CancellationToken>()).Returns(discarded);

        var result = await _sut.AbandonChunkedUpload(1, "u", TestContext.Current.CancellationToken);

        Assert.IsType(expected, result);
    }

    // --- Cross-agent deploy download ---

    [Fact]
    public async Task AgentDownloadArtifact_WithoutServerIdClaim_IsForbiddenBeforeOpeningAnything()
    {
        var result = await _sut.AgentDownloadArtifact(3, 9, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
        await _service.DidNotReceiveWithAnyArgs().OpenArtifactForAgentAsync(default, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AgentDownloadArtifact_Allowed_StreamsTheZipUnderItsName()
    {
        SetAgentUser(serverId: 7);
        using var content = new MemoryStream([1, 2, 3]);
        _service.OpenArtifactForAgentAsync(3, 9, 7, Arg.Any<CancellationToken>())
            .Returns((DeployDownloadStatus.Ok, (Stream?)content, (string?)"web.zip"));

        var result = await _sut.AgentDownloadArtifact(3, 9, TestContext.Current.CancellationToken);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Same(content, file.FileStream);
        Assert.Equal(("application/zip", "web.zip"), (file.ContentType, file.FileDownloadName));
    }

    [Theory]
    [InlineData(DeployDownloadStatus.Forbidden, typeof(ForbidResult))]
    [InlineData(DeployDownloadStatus.NotFound, typeof(NotFoundResult))]
    public async Task AgentDownloadArtifact_Refused_MapsTheServiceVerdict(DeployDownloadStatus status, Type expected)
    {
        SetAgentUser(serverId: 7);
        _service.OpenArtifactForAgentAsync(3, 9, 7, Arg.Any<CancellationToken>())
            .Returns((status, (Stream?)null, (string?)null));

        Assert.IsType(expected, await _sut.AgentDownloadArtifact(3, 9, TestContext.Current.CancellationToken));
    }

    private static readonly string Digest = new('0', 64);

    private void AssignAgent()
    {
        SetAgentUser(serverId: 7);
        _service.IsAgentAssignedToRunAsync(1, 7, Arg.Any<CancellationToken>()).Returns(true);
    }

    private void SetAgentUser(int serverId)
    {
        _sut.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("ServerId", serverId.ToString())], "agent"));
        _sut.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        _sut.ControllerContext.HttpContext.Request.ContentLength = 7;
    }
}
