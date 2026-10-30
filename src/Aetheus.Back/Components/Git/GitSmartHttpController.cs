// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Authentication;

namespace Aetheus.Back.Components.Git;

[ApiController]
[Route("git")]
public class GitSmartHttpController(IGitSmartHttpService smartHttp, IResourceAuthorizationService authz) : ControllerBase
{
    [Authorize(AuthenticationSchemes = GitBasicAuthenticationHandler.SchemeName)]
    [HttpGet("{projectId:int}/{slug}.git/info/refs")]
    public async Task<IActionResult> GetInfoRefs(int projectId, string slug, [FromQuery] string service, CancellationToken ct)
    {
        var required = string.Equals(service, "git-receive-pack", StringComparison.Ordinal) ? Permission.Write : Permission.Read;
        if (!(required == Permission.Read && IsRunScoped(projectId))
            && !await authz.HasPermissionAsync(User, ResourceType.Project, projectId, required, ct))
            return Forbid();

        var result = await smartHttp.GetInfoRefsAsync(projectId, slug, service, ct);
        if (result is null) return NotFound();

        return File(result.Value.Body, result.Value.ContentType);
    }

    [Authorize(AuthenticationSchemes = GitBasicAuthenticationHandler.SchemeName)]
    [HttpPost("{projectId:int}/{slug}.git/git-upload-pack")]
    public async Task<IActionResult> UploadPack(int projectId, string slug, CancellationToken ct)
    {
        if (!IsRunScoped(projectId)
            && !await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Read, ct))
            return Forbid();

        var result = await ExecuteServiceAsync(projectId, slug, "git-upload-pack", ct);
        if (result is null) return NotFound();

        return new FileStreamResult(result.Body, result.ContentType);
    }

    [Authorize(AuthenticationSchemes = GitBasicAuthenticationHandler.SchemeName)]
    [HttpPost("{projectId:int}/{slug}.git/git-receive-pack")]
    // Git pushes carry packfiles in the HTTP body. The Kestrel default (10 MiB) rejects ordinary
    // source repositories before Git can report a useful protocol error; keep the larger bound
    // deliberately scoped to receive-pack rather than raising the application-wide request limit.
    [RequestSizeLimit(512L * 1024 * 1024)]
    public async Task<IActionResult> ReceivePack(int projectId, string slug, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, Permission.Write, ct))
            return Forbid();

        var result = await ExecuteServiceAsync(projectId, slug, "git-receive-pack", ct);
        if (result is null) return NotFound();

        // Mark as pushed after successful receive
        await smartHttp.MarkPushedAsync(projectId, slug, result.UpdatedRefs, ct, User.Identity?.Name);

        return new FileStreamResult(result.Body, result.ContentType);
    }

    // A pipeline-run clone token (GitRunCloneToken) authorises READ access to exactly the project it
    // was minted for - never write - so the run can clone its own mirror without a real user grant.
    private bool IsRunScoped(int projectId) =>
        User.HasClaim(GitBasicAuthenticationHandler.RunScopeClaim, projectId.ToString());

    private async Task<GitSmartHttpResponse?> ExecuteServiceAsync(
        int projectId,
        string slug,
        string service,
        CancellationToken ct)
    {
        await using var body = GitSmartHttpRequestBody.Open(Request);
        return await smartHttp.ExecuteServiceAsync(projectId, slug, service, body, ct);
    }
}
