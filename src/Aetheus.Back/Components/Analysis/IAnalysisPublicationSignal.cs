// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// Tells the people watching a pipeline run that an analysis report is being published for it (R1-12):
/// publishing a large report takes seconds, and nothing else on the run page says so meanwhile.
/// </summary>
public interface IAnalysisPublicationSignal
{
    /// <summary>Announces that the publication for <paramref name="runId"/> started or ended.</summary>
    Task AnnounceAsync(int runId, bool inProgress, CancellationToken ct = default);
}

/// <summary>Sends the announcement to the run's SignalR group as <c>AnalysisPublishing(runId, inProgress)</c>.</summary>
internal sealed class PipelineHubAnalysisPublicationSignal(IHubContext<PipelineHub> pipelineHub) : IAnalysisPublicationSignal
{
    public Task AnnounceAsync(int runId, bool inProgress, CancellationToken ct = default) =>
        pipelineHub.Clients.Group(HubGroups.PipelineRun(runId)).SendAsync("AnalysisPublishing", runId, inProgress, ct);
}
