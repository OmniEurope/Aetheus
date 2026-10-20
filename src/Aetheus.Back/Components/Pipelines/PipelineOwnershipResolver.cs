// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Http;

namespace Aetheus.Back.Components.Pipelines;

internal sealed class PipelineOwnershipResolver(
    IPipelineProjectOwnerReader repository,
    IHttpContextAccessor httpContextAccessor,
    ILogger<PipelineService> logger)
{
    internal string? CurrentUsername =>
        httpContextAccessor.HttpContext?.User?.Identity?.Name;

    internal async Task<string?> ResolveAsync(int projectId, CancellationToken ct)
    {
        var owner = CurrentUsername;
        if (string.IsNullOrEmpty(owner))
            owner = await repository.GetProjectOwnerUsernameAsync(projectId, ct).ConfigureAwait(false);
        return owner;
    }

    internal async Task<bool> IsUnresolvableAsync(string? owner, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(owner)
            || string.Equals(owner, "system", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !await repository.IsActiveUsernameAsync(owner, ct).ConfigureAwait(false);
    }

    internal ConflictException MissingOwner(int projectId, string pipelineName)
    {
        logger.LogError(
            "Cannot synchronize pipeline '{PipelineName}' for project {ProjectId}: no authenticated pusher or active organization owner could be resolved",
            pipelineName,
            projectId);
        return new ConflictException(
            $"Pipeline '{pipelineName}' cannot be synchronized because project {projectId} has no authenticated pusher or organization owner. Assign an organization Owner and retry.");
    }
}
