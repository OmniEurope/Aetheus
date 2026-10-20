// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Hubs;

[Authorize]
public class PipelineHub(IPipelineRunLookupReader runLookup, IResourceAuthorizationService authz) : Hub
{
    public async Task JoinPipelineRunGroup(int runId)
    {
        var pipelineId = await runLookup.GetPipelineIdForRunAsync(runId).ConfigureAwait(false);
        if (pipelineId is null)
            throw new HubException("Run not found.");

        if (!await authz.HasPermissionAsync(Context.User!, ResourceType.Pipeline, pipelineId.Value, Permission.Read).ConfigureAwait(false))
            throw new HubException("Access denied.");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"pipeline-run-{runId}").ConfigureAwait(false);
    }

    public Task LeavePipelineRunGroup(int runId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"pipeline-run-{runId}");

    public async Task JoinPipelineUpdatesGroup()
    {
        // F-05: fan-out per accessible pipeline. Admins keep the global aggregate group.
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(Context.User!, ResourceType.Pipeline, Permission.Read).ConfigureAwait(false);
        if (accessibleIds is null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.PipelineUpdates).ConfigureAwait(false);
            return;
        }
        if (accessibleIds.Count == 0)
            throw new HubException("Access denied.");
        foreach (var id in accessibleIds)
            await Groups.AddToGroupAsync(Context.ConnectionId, $"pipeline-{id}").ConfigureAwait(false);
    }

    public async Task LeavePipelineUpdatesGroup()
    {
        var accessibleIds = await authz.GetAccessibleResourceIdsAsync(Context.User!, ResourceType.Pipeline, Permission.Read).ConfigureAwait(false);
        if (accessibleIds is null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, HubGroups.PipelineUpdates).ConfigureAwait(false);
            return;
        }
        foreach (var id in accessibleIds)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"pipeline-{id}").ConfigureAwait(false);
    }
}
