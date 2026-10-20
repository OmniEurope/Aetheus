// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Artifacts;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ArtifactsController(
    IArtifactService artifactService,
    IChunkedArtifactUploadService chunkedUploads,
    IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet("project/{projectId:int}")]
    public async Task<ActionResult<PaginatedResult<PipelineArtifactDto>>> GetProjectArtifacts(
        int projectId, [FromQuery] ProjectArtifactsRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();

        return Ok(await artifactService.GetProjectArtifactsAsync(projectId, request, ct));
    }

    /// <summary>The pipeline and environment names the project artifacts grid's column filters
    /// offer.</summary>
    [HttpGet("project/{projectId:int}/filter-values")]
    public async Task<ActionResult<ProjectArtifactFilterValuesDto>> GetProjectArtifactFilterValues(
        int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();

        return Ok(await artifactService.GetProjectArtifactFilterValuesAsync(projectId, ct));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PipelineArtifactDto>> GetArtifact(int id, CancellationToken ct)
    {
        var (artifact, failure) = await GetAuthorizedArtifactAsync(id, Permission.Read, ct);
        if (failure is not null) return failure;

        return Ok(artifact!);
    }

    [HttpGet("{id:int}/download")]
    public async Task<IActionResult> DownloadArtifact(int id, CancellationToken ct)
    {
        var (artifact, failure) = await GetAuthorizedArtifactAsync(id, Permission.Read, ct);
        if (failure is not null) return failure;

        var stream = await artifactService.DownloadArtifactAsync(id, ct);
        if (stream is null) return NotFound();

        return File(stream, "application/zip", $"{artifact!.Name}.zip");
    }

    // Cross-agent deploy download. AgentToken-authorised, IDOR-safe: the service checks the agent is
    // assigned to the DEPLOY run it passes (not the artifact's build run - scenario 3) and that the
    // artifact's project org equals the agent server's org. The deploy run id travels as a query param.
    [HttpGet("agent-download/{id:int}")]
    [Authorize(Policy = "AgentToken")]
    public async Task<IActionResult> AgentDownloadArtifact(int id, [FromQuery] int runId, CancellationToken ct)
    {
        var serverIdClaim = User.FindFirst("ServerId")?.Value;
        if (!int.TryParse(serverIdClaim, out var agentServerId)) return Forbid();

        var (status, stream, fileName) = await artifactService.OpenArtifactForAgentAsync(id, runId, agentServerId, ct);
        return status switch
        {
            DeployDownloadStatus.Ok => File(stream!, "application/zip", fileName),
            DeployDownloadStatus.Forbidden => Forbid(),
            _ => NotFound()
        };
    }

    // Upload ceiling rationale (DoS analysis): the endpoint is [Authorize(AgentToken)] - only an
    // enrolled agent can POST here, and IsAgentAssignedToRunAsync further pins it to a run the agent is
    // actually executing, so the surface is "a compromised/enrolled agent", not the open internet.
    // Sizing: a `docker save` of a .NET runtime image measures ~700 MiB; gzipped it still clears 500 MiB,
    // which is why the old 500 MiB cap 413'd the very payload container deploy needs - 1 GiB is the
    // smallest power-of-two ceiling that admits a realistic container artifact with headroom. Tune the
    // const if your images are larger; it bounds the per-request body buffer either way.
    private const long MaxArtifactUploadBytes = 1024L * 1024 * 1024; // 1 GiB

    [HttpPost("upload/{runId:int}")]
    [Authorize(Policy = "AgentToken")]
    [RequestSizeLimit(MaxArtifactUploadBytes)]
    public async Task<ActionResult<PipelineArtifactDto>> UploadArtifact(
        int runId, [FromQuery] string name, [FromQuery] string? stageName, CancellationToken ct)
    {
        var serverIdClaim = User.FindFirst("ServerId")?.Value;
        if (!int.TryParse(serverIdClaim, out var agentServerId)) return Forbid();
        if (!await artifactService.IsAgentAssignedToRunAsync(runId, agentServerId, ct)) return Forbid();

        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return BadRequest("Invalid artifact name");
        // Bound stageName to the same limit as PublishArtifactRequest.StageName ([StringLength(200)]):
        // these query params bypass the DTO validator, so the cap is enforced here before persistence.
        if (stageName is { Length: > 200 }) return BadRequest("Invalid stage name");

        // The agent uploads a seekable ZIP and therefore always provides Content-Length. Requiring
        // it lets quota eviction run before the body is streamed and prevents quota checks from
        // accepting an artifact whose size is unknown.
        if (Request.ContentLength is not long contentLength)
            return StatusCode(StatusCodes.Status411LengthRequired, "Content-Length is required for artifact uploads.");

        var result = await artifactService.PublishArtifactAsync(runId, name, stageName, contentLength, Request.Body, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }

    // Chunked uploads declare and verify digests with MaxArtifactUploadBytes as the per-part cap.
    // Single-request upload remains supported for compatible agents.

    [HttpPost("upload/{runId:int}/begin")]
    [Authorize(Policy = "AgentToken")]
    public async Task<ActionResult<ChunkedUploadSession>> BeginChunkedUpload(
        int runId,
        [FromQuery] string name,
        [FromQuery] string? stageName,
        [FromQuery] int totalParts,
        [FromQuery] string sha256,
        CancellationToken ct)
    {
        if (await AgentIsAssignedAsync(runId, ct) is { } failure) return failure;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return BadRequest("Invalid artifact name");
        if (stageName is { Length: > 200 }) return BadRequest("Invalid stage name");

        return Ok(await chunkedUploads.BeginAsync(runId, name, stageName, totalParts, sha256, ct));
    }

    [HttpPost("upload/{runId:int}/part/{uploadId}/{index:int}")]
    [Authorize(Policy = "AgentToken")]
    [RequestSizeLimit(MaxArtifactUploadBytes)]
    public async Task<ActionResult<ChunkedPartResult>> UploadChunkedPart(
        int runId, string uploadId, int index, [FromQuery] string sha256, CancellationToken ct)
    {
        if (await AgentIsAssignedAsync(runId, ct) is { } failure) return failure;

        var result = await chunkedUploads.AcceptPartAsync(runId, uploadId, index, sha256, Request.Body, ct);
        // A refused part is a client error the agent retries, not a server fault: it says so, and
        // says which part, so a resend targets that part rather than the whole artifact.
        return result.Accepted ? Ok(result) : BadRequest(result);
    }

    [HttpPost("upload/{runId:int}/complete/{uploadId}")]
    [Authorize(Policy = "AgentToken")]
    public async Task<ActionResult<PipelineArtifactDto>> CompleteChunkedUpload(
        int runId, string uploadId, [FromQuery] string name, [FromQuery] string? stageName, CancellationToken ct)
    {
        if (await AgentIsAssignedAsync(runId, ct) is { } failure) return failure;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return BadRequest("Invalid artifact name");
        if (stageName is { Length: > 200 }) return BadRequest("Invalid stage name");

        // Assembled and digest-checked before anything is published: an artifact that does not match
        // what was announced never reaches the content-addressed store. The verified file is then
        // moved into the store, not copied (recette R1-91).
        var result = await chunkedUploads.CompleteAsync(runId, uploadId,
            verified => artifactService.PublishVerifiedArtifactAsync(runId, name, stageName, verified, ct), ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("upload/{runId:int}/{uploadId}")]
    [Authorize(Policy = "AgentToken")]
    public async Task<IActionResult> AbandonChunkedUpload(int runId, string uploadId, CancellationToken ct)
    {
        if (await AgentIsAssignedAsync(runId, ct) is { } failure) return failure;
        // AUD-06: an agent assigned to this run cannot abandon another run's upload by its id.
        return await chunkedUploads.DiscardForRunAsync(runId, uploadId, ct) ? NoContent() : NotFound();
    }

    /// <summary>The agent-token check every upload endpoint shares: enrolled, and executing THIS run.</summary>
    private async Task<ActionResult?> AgentIsAssignedAsync(int runId, CancellationToken ct)
    {
        var serverIdClaim = User.FindFirst("ServerId")?.Value;
        if (!int.TryParse(serverIdClaim, out var agentServerId)) return Forbid();
        return await artifactService.IsAgentAssignedToRunAsync(runId, agentServerId, ct) ? null : Forbid();
    }

    [HttpPost("{id:int}/promote")]
    public async Task<ActionResult<PipelineArtifactDto>> PromoteArtifact(
        int id, [FromBody] PromoteArtifactRequest request, CancellationToken ct)
    {
        var (_, failure) = await GetAuthorizedArtifactAsync(id, Permission.Write, ct);
        if (failure is not null) return failure;

        if (string.IsNullOrWhiteSpace(request.EnvironmentName))
            return BadRequest("EnvironmentName is required");

        var result = await artifactService.PromoteToEnvironmentAsync(id, request.EnvironmentName, ct);
        return Ok(result!);
    }

    private async Task<(PipelineArtifactDto? Artifact, ActionResult? Failure)> GetAuthorizedArtifactAsync(
        int artifactId,
        Permission permission,
        CancellationToken ct)
    {
        var artifact = await artifactService.GetArtifactAsync(artifactId, ct);
        if (artifact is null) return (null, NotFound());

        // Fail-closed, and resolved transitively. The artifact's own ProjectId is null by design for
        // a pipeline owned by an Environment or a ProjectServer, so authorizing on that column alone
        // let any authenticated caller read, download and promote those artifacts. An owner that
        // cannot be named is a refusal: there is no permission to check, not no permission needed.
        var owningProjectId = await artifactService.GetOwningProjectIdAsync(artifactId, ct);
        if (owningProjectId is not { } projectId) return (null, Forbid());
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, permission, ct))
            return (null, Forbid());
        return (artifact, null);
    }
}
