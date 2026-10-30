// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Docker;

[ApiController]
[Route("api/servers/{serverId:int}/docker")]
[Authorize]
[ServiceFilter(typeof(ValidateServerExistsFilter))]
public class DockerController(IDockerService dockerService, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet("containers")]
    public async Task<ActionResult<List<DockerContainerDto>>> GetContainers(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var containers = await dockerService.GetContainersAsync(serverId, ct);
        return Ok(containers);
    }

    [HttpPost("action")]
    public async Task<IActionResult> ExecuteAction(int serverId, [FromBody] DockerActionRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await dockerService.ExecuteActionAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("containers/logs")]
    public async Task<ActionResult<string>> GetContainerLogs(int serverId, [FromBody] DockerContainerLogsRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var result = await dockerService.GetContainerLogsAsync(serverId, request, ct);
        if (string.IsNullOrEmpty(result)) return NotFound();
        return Ok(result);
    }

    [HttpGet("images")]
    public async Task<ActionResult<List<DockerImageDto>>> GetImages(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var images = await dockerService.GetImagesAsync(serverId, ct);
        return Ok(images);
    }

    [HttpPost("images/pull")]
    public async Task<IActionResult> PullImage(int serverId, [FromBody] DockerPullImageRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await dockerService.PullImageAsync(serverId, request, ct);
        return Ok();
    }

    [HttpDelete("images/{imageId}")]
    public async Task<IActionResult> RemoveImage(int serverId, string imageId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct))
            return Forbid();
        await dockerService.RemoveImageAsync(serverId, imageId, ct);
        return Ok();
    }

    [HttpGet("compose")]
    public async Task<ActionResult<List<DockerComposeStackDto>>> GetComposeStacks(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var stacks = await dockerService.GetComposeStacksAsync(serverId, ct);
        return Ok(stacks);
    }

    [HttpPost("compose/action")]
    public async Task<IActionResult> ExecuteComposeAction(int serverId, [FromBody] DockerComposeActionRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await dockerService.ExecuteComposeActionAsync(serverId, request, ct);
        return Ok();
    }

    [HttpGet("networks")]
    public async Task<ActionResult<List<DockerNetworkDto>>> GetNetworks(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var networks = await dockerService.GetNetworksAsync(serverId, ct);
        return Ok(networks);
    }

    [HttpGet("volumes")]
    public async Task<ActionResult<List<DockerVolumeDto>>> GetVolumes(int serverId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        var volumes = await dockerService.GetVolumesAsync(serverId, ct);
        return Ok(volumes);
    }

    [HttpPost("prune")]
    public async Task<IActionResult> Prune(int serverId, [FromBody] DockerPruneRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct))
            return Forbid();
        await dockerService.PruneAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("resource-limits")]
    public async Task<IActionResult> UpdateResourceLimits(int serverId, [FromBody] DockerResourceLimitsRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Write, ct))
            return Forbid();
        await dockerService.UpdateResourceLimitsAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("containers/{containerId}/inspect")]
    public async Task<IActionResult> InspectContainer(int serverId, string containerId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        await dockerService.InspectContainerAsync(serverId, containerId, ct);
        return Ok();
    }

    [HttpGet("compose/{stackName}/file")]
    public async Task<IActionResult> GetComposeFile(int serverId, string stackName, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        await dockerService.GetComposeFileAsync(serverId, stackName, ct);
        return Ok();
    }

    [HttpPut("compose/file")]
    public async Task<IActionResult> SaveComposeFile(int serverId, [FromBody] DockerComposeFileSaveRequest request, CancellationToken ct)
    {
        // BACK-012: an arbitrary Compose file runs arbitrary containers (privileged, host mounts) on
        // the server, the same reach as exec, so it takes the same Admin permission.
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct))
            return Forbid();
        await dockerService.SaveComposeFileAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("exec")]
    public async Task<IActionResult> ExecuteShellCommand(int serverId, [FromBody] DockerExecRequest request, CancellationToken ct)
    {
        // Hardening (High #10): arbitrary `docker exec` is highest-risk capability - gate behind
        // Admin permission, not Write, and emit a dedicated audit row. Permission.Write is
        // already required to operate on the server, but exec specifically deserves Admin.
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct))
            return Forbid();
        await dockerService.ExecuteShellCommandAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("containers/{containerId}/env")]
    public async Task<IActionResult> GetContainerEnvVars(int serverId, string containerId, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, PermissionKeys.ServerDockerContainerEnvView, serverId, ct))
            return Forbid();
        await dockerService.GetContainerEnvVarsAsync(serverId, containerId, ct);
        return Ok();
    }

    [HttpPost("containers/browse")]
    public async Task<IActionResult> ListContainerFiles(int serverId, [FromBody] DockerBrowseRequest request, CancellationToken ct)
    {
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Read, ct))
            return Forbid();
        await dockerService.ListContainerFilesAsync(serverId, request, ct);
        return Ok();
    }

    [HttpPost("build")]
    public async Task<IActionResult> BuildImage(int serverId, [FromBody] DockerBuildRequest request, CancellationToken ct)
    {
        // BACK-012: a Dockerfile's RUN steps are arbitrary commands executed by the server's Docker
        // daemon, so building one takes Admin like exec.
        if (!await authz.HasPermissionAsync(User, ResourceType.Server, serverId, Permission.Admin, ct))
            return Forbid();
        await dockerService.BuildImageAsync(serverId, request, ct);
        return Ok();
    }
}
