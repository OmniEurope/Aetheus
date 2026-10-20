// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Authorizes the owner a pipeline is being given, beyond the project the controller checks.</summary>
public interface IPipelineOwnerAuthorization
{
    /// <summary>Checks every supplied project, environment and project-server owner for pipeline
    /// ownership authorization. An unresolved owner is refused rather than skipped.</summary>
    Task<bool> CanOwnAsync(
        ClaimsPrincipal user, int? projectId, int? environmentId, int? projectServerId, CancellationToken ct = default);
}

/// <inheritdoc cref="IPipelineOwnerAuthorization"/>
public sealed class PipelineOwnerAuthorization(
    IPipelineService pipelineService,
    IResourceAuthorizationService authz) : IPipelineOwnerAuthorization
{
    public async Task<bool> CanOwnAsync(
        ClaimsPrincipal user, int? projectId, int? environmentId, int? projectServerId, CancellationToken ct = default)
    {
        // Nested rather than `projectId is { } id && !await ...`: in that single condition the
        // authorization call is never reached when the id is null, which reads as a guard but is an
        // absence. Here the null case is a decision of its own, taken by falling through to the next
        // owner, and NullMeansAuthorizedAuditTests can see it.
        if (projectId is { } ownerProjectId)
        {
            if (!await authz.HasPermissionAsync(user, ResourceType.Project, ownerProjectId, Permission.Write, ct)
                    .ConfigureAwait(false))
                return false;
        }

        if (environmentId is { } id)
        {
            if (!await authz.HasPermissionAsync(user, ResourceType.Environment, id, Permission.Write, ct)
                    .ConfigureAwait(false))
                return false;
        }

        if (projectServerId is not { } serverId) return true;

        // There is no ResourceType.ProjectServer, so the row's owning project carries the permission.
        var owningProjectId = await pipelineService.GetProjectServerProjectIdAsync(serverId, ct).ConfigureAwait(false);
        if (owningProjectId is not { } serverProjectId) return false;
        return await authz.HasPermissionAsync(user, ResourceType.Project, serverProjectId, Permission.Write, ct)
            .ConfigureAwait(false);
    }
}
