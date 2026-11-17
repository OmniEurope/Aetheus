// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// The vulnerability watch announces each matching pass, and this finds its own work: the projects whose
/// matches and findings disagree. Nothing is carried from one pass to the next, so a project whose
/// publication failed (quota, conflict, database) is simply found again at the next pass.
/// </summary>
internal sealed class CveWatchPublication(
    CveWatchReportRepository watch,
    CveWatchReportPublisher publisher,
    ILogger<CveWatchPublication> logger) : IDomainEventHandler<VulnerabilityMatchesChangedEvent>
{
    private const int ProjectChunk = 100;

    public async Task HandleAsync(VulnerabilityMatchesChangedEvent domainEvent, CancellationToken ct = default)
    {
        var reports = 0;
        var failed = 0;
        var afterProjectId = 0;
        while (await watch.GetCandidateProjectIdsAsync(afterProjectId, ProjectChunk, ct).ConfigureAwait(false) is { Count: > 0 } projects)
        {
            foreach (var projectId in projects)
            {
                try
                {
                    reports += await publisher.PublishProjectAsync(projectId, ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failed++;
                    // The failed write may still be tracked: left there, the next project would try to save it too.
                    watch.DiscardUnsavedChanges();
                    logger.LogError(exception, "CVE watch publication failed for project {ProjectId}; it stays pending", projectId);
                }
            }

            afterProjectId = projects[^1];
        }

        if (reports > 0 || failed > 0)
            logger.LogInformation("CVE watch publication: {Reports} reports published, {Failed} projects failed", reports, failed);
    }
}
