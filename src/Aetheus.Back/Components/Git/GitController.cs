// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Git;

[ApiController]
[Route("api/git")]
[Authorize]
public class GitController(IGitService gitService, IResourceAuthorizationService authz) : ControllerBase
{
    // Git connections.

    [HttpGet("connections")]
    public async Task<ActionResult<List<GitConnectionDto>>> GetConnections([FromQuery] int projectId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();
        return Ok(await gitService.GetConnectionsByProjectAsync(projectId, ct));
    }

    [HttpGet("connections/{id:int}")]
    public async Task<ActionResult<GitConnectionDto>> GetConnection(int id, CancellationToken ct)
    {
        // F-02: enforce per-project Read on all connection-scoped operations.
        var projectId = await gitService.GetProjectIdForConnectionAsync(id, ct);
        if (projectId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Read, ct))
            return Forbid();

        var result = await gitService.GetConnectionDetailAsync(id, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }

    [HttpPost("connections")]
    public async Task<ActionResult<GitConnectionDto>> CreateConnection([FromBody] CreateGitConnectionRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, request.ProjectId, Permission.Write, ct))
            return Forbid();
        if (!await CanUseServiceConnectionAsync(request.ProjectId, request.ServiceConnectionId, ct))
            return Forbid();
        var result = await gitService.CreateConnectionAsync(request, ct);
        return CreatedAtAction(nameof(GetConnection), new { id = result.Id }, result);
    }

    [HttpPut("connections/{id:int}")]
    public async Task<ActionResult<GitConnectionDto>> UpdateConnection(int id, [FromBody] UpdateGitConnectionRequest request, CancellationToken ct)
    {
        // F-02: enforce per-project Write before mutating a connection.
        var projectId = await gitService.GetProjectIdForConnectionAsync(id, ct);
        if (projectId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Write, ct))
            return Forbid();
        if (!await CanUseServiceConnectionAsync(projectId.Value, request.ServiceConnectionId, ct))
            return Forbid();

        var result = await gitService.UpdateConnectionAsync(id, request, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }

    [HttpDelete("connections/{id:int}")]
    public async Task<IActionResult> DeleteConnection(int id, CancellationToken ct)
    {
        // F-02: enforce per-project Admin before deleting a connection.
        var projectId = await gitService.GetProjectIdForConnectionAsync(id, ct);
        if (projectId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Admin, ct))
            return Forbid();

        var deleted = await gitService.DeleteConnectionAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    // Pull requests.

    [HttpGet("connections/{gitConnectionId:int}/pull-requests")]
    public async Task<ActionResult<PaginatedResult<PullRequestDto>>> GetPullRequests(
        int gitConnectionId, [FromQuery] PullRequestPaginationRequest request, CancellationToken ct)
    {
        // F-02: enforce per-project Read on PR listing.
        var projectId = await gitService.GetProjectIdForConnectionAsync(gitConnectionId, ct);
        if (projectId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Read, ct))
            return Forbid();

        return Ok(await gitService.GetPullRequestsAsync(gitConnectionId, request, ct));
    }

    [HttpPost("connections/{gitConnectionId:int}/pull-requests/sync")]
    public async Task<ActionResult<PullRequestDto>> SyncPullRequest(
        int gitConnectionId, [FromBody] PullRequestDto incoming, CancellationToken ct)
    {
        // F-02: enforce per-project Write before mutating PR data.
        var projectId = await gitService.GetProjectIdForConnectionAsync(gitConnectionId, ct);
        if (projectId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Write, ct))
            return Forbid();

        var result = await gitService.SyncPullRequestAsync(gitConnectionId, incoming.ExternalId, incoming, ct);
        if (result is null) return BadRequest();
        return Ok(result);
    }

    // Branch policies.

    [HttpGet("connections/{gitConnectionId:int}/branch-policies")]
    public async Task<ActionResult<List<BranchPolicyDto>>> GetBranchPolicies(int gitConnectionId, CancellationToken ct)
    {
        // F-02: enforce per-project Read.
        var projectId = await gitService.GetProjectIdForConnectionAsync(gitConnectionId, ct);
        if (projectId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Read, ct))
            return Forbid();

        return Ok(await gitService.GetBranchPoliciesAsync(gitConnectionId, ct));
    }

    [HttpPost("branch-policies")]
    public async Task<ActionResult<BranchPolicyDto>> CreateBranchPolicy([FromBody] CreateBranchPolicyRequest request, CancellationToken ct)
    {
        // F-02: enforce per-project Write before creating a branch policy.
        var projectId = await gitService.GetProjectIdForConnectionAsync(request.GitConnectionId, ct);
        if (projectId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Write, ct))
            return Forbid();

        var result = await gitService.CreateBranchPolicyAsync(request, ct);
        return CreatedAtAction(nameof(GetBranchPolicies), new { gitConnectionId = request.GitConnectionId }, result);
    }

    [HttpPut("branch-policies/{id:int}")]
    public async Task<ActionResult<BranchPolicyDto>> UpdateBranchPolicy(int id, [FromBody] UpdateBranchPolicyRequest request, CancellationToken ct)
    {
        // F-02: enforce per-project Write.
        var projectId = await gitService.GetProjectIdForBranchPolicyAsync(id, ct);
        if (projectId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Write, ct))
            return Forbid();

        var result = await gitService.UpdateBranchPolicyAsync(id, request, ct);
        if (result is null) return NotFound();
        return Ok(result);
    }

    [HttpDelete("branch-policies/{id:int}")]
    public async Task<IActionResult> DeleteBranchPolicy(int id, CancellationToken ct)
    {
        // F-02: enforce per-project Admin before deletion.
        var projectId = await gitService.GetProjectIdForBranchPolicyAsync(id, ct);
        if (projectId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Admin, ct))
            return Forbid();

        var deleted = await gitService.DeleteBranchPolicyAsync(id, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }

    // --- P-43: PR Status Reporting ---

    [HttpPost("status-report")]
    public async Task<IActionResult> ReportPipelineStatus([FromBody] PipelineStatusReport report, CancellationToken ct)
    {
        // F-02: report.PipelineRunId belongs to a project; require Write on that project.
        // The status-report endpoint is invoked by pipeline runners on behalf of users; we still
        // require the calling user to have Write on the originating project. (If callers are agents,
        // they already use [Authorize(Policy = "AgentToken")] on dedicated endpoints.)
        var projectId = await gitService.GetProjectIdForRunAsync(report.PipelineRunId, ct);
        if (projectId is null) return NotFound();
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId.Value, Permission.Write, ct))
            return Forbid();

        await gitService.ReportPipelineStatusAsync(report, ct);
        // Honest contract (audit P-43): the status is recorded locally only. Forwarding to the external
        // provider's commit-status API (GitHub/GitLab) is not yet wired, so the response says so plainly
        // instead of a bare 200 that implies the provider was updated.
        return Ok(new { recorded = true, forwardedToProvider = false });
    }

    /// <summary>M4: the credentials a connection mirrors with are a service connection the caller can read
    /// and that belongs to the project's organization, never another organization's.</summary>
    private async Task<bool> CanUseServiceConnectionAsync(int projectId, int? serviceConnectionId, CancellationToken ct)
    {
        if (serviceConnectionId is not { } connectionId)
            return true; // no credential named: the connection mirrors anonymously
        return await authz.HasPermissionAsync(User, ResourceType.ServiceConnection, connectionId, Permission.Read, ct)
            && await authz.BelongsToProjectOrganizationAsync(projectId, ResourceType.ServiceConnection, [connectionId], ct);
    }
}
