// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Projects;

/// <summary>
/// Recette R-97 (ADR-058): the server side of a project deletion. The confirmation asks the agents what the
/// project deployed (discovery), removes exactly what they found once the user confirmed (cleanup), and reads
/// the tasks back as they run. Every endpoint needs the same permission as the deletion itself (Admin on the
/// project, granted within the project's organization), and every task read or used must be one of this
/// project's (its operation, its target, a server of the project's organization). Admin on the project is not a
/// right on its servers: the service reads and removes only on the servers where the current user holds Write
/// (<see cref="IProjectServerCleanupGuardSource"/>); the others are listed as not removed.
/// </summary>
[ApiController]
[Route("api/projects/{id:int}/deletion")]
[Authorize]
[ProducesResponseType(StatusCodes.Status200OK)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
public class ProjectDeletionController(
    IResourceAuthorizationService authz,
    IProjectServerCleanupService serverCleanup) : ControllerBase
{
    /// <summary>Asks the agent of each reachable server what the project deployed there.</summary>
    [HttpPost("discovery")]
    public async Task<ActionResult<List<ProjectServerResourceTaskDto>>> Discover(int id, CancellationToken ct)
    {
        if (!await CanDeleteAsync(id, ct)) return Forbid();
        return Ok(await serverCleanup.QueueDiscoveryAsync(id, ct));
    }

    /// <summary>The project's server tasks among <paramref name="taskIds"/>, as far as they went.</summary>
    [HttpGet("tasks")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<ProjectServerResourceTaskDto>>> GetTasks(
        int id, [FromQuery] List<int>? taskIds, CancellationToken ct)
    {
        if (!await CanDeleteAsync(id, ct)) return Forbid();
        if (taskIds is { Count: > ProjectServerResourceLimits.MaxTaskIds })
            return BadRequest($"At most {ProjectServerResourceLimits.MaxTaskIds} tasks.");
        return Ok(await serverCleanup.GetTasksAsync(id, taskIds ?? [], ct));
    }

    /// <summary>Removes, on each server, exactly what its discovery found; 400 when volumes are listed and
    /// their data loss is not acknowledged, 409 while a run of the project is in progress.</summary>
    [HttpPost("cleanup")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<List<ProjectServerResourceTaskDto>>> Cleanup(
        int id, [FromBody] ProjectServerCleanupRequest request, CancellationToken ct)
    {
        if (!await CanDeleteAsync(id, ct)) return Forbid();
        return Ok(await serverCleanup.QueueRemovalAsync(id, request, ct));
    }

    private Task<bool> CanDeleteAsync(int id, CancellationToken ct) =>
        authz.HasPermissionAsync(User, ResourceType.Project, id, Permission.Admin, ct);
}
