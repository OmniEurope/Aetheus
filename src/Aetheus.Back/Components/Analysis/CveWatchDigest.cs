// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Shared.Components.Notifications;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// The daily mail of the vulnerability watch (recette R-25, R-26): ONE <c>analysis.cve.detected</c> event for all the
/// projects, listing only the matches worth a mail (Critical, published High, exploited) that no mail has told yet.
/// Nothing new, no mail: what is still open is said by the daily recap, not repeated here, so the user is not flooded
/// (user decision of 2026-11-03). A published High is told here for the first time; a severe one already left at
/// once (<see cref="CveWatchAnnouncement"/>). The recipient reads the projects they follow, so the mail
/// of one person never names a project they cannot read. Matches told for the first time are claimed
/// (<c>NotifiedAt</c>) before the event leaves and given back if sending fails, so a failed night is made up by
/// the next one.
/// </summary>
internal sealed class CveWatchDigest(
    CveWatchAnnouncementRepository repository,
    CveWatchHostAnnouncementRepository hostRepository,
    INotificationService notifications,
    TimeProvider timeProvider,
    ILogger<CveWatchDigest> logger)
{
    private const int ProjectChunk = 100;

    /// <summary>Sends the digest; returns how many new matches and host findings it listed (0: nothing new, nothing sent).
    /// R-41: the CVE of the servers' host systems are told with the projects their server belongs to, by the same rule.</summary>
    public async Task<int> SendAsync(CancellationToken ct)
    {
        var fresh = (await ReadOpenAsync(ct).ConfigureAwait(false)).FindAll(match => match.IsNew);
        var freshHost = (await hostRepository.GetOpenAsync(ct).ConfigureAwait(false)).FindAll(finding => finding.IsNew);
        if (fresh.Count == 0 && freshHost.Count == 0) return 0;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var claimed = fresh.Count == 0
            ? []
            : await repository.ClaimAsync([.. fresh.Select(match => match.MatchId)], now, ct).ConfigureAwait(false);
        var claimedHost = new List<long>();
        try
        {
            if (freshHost.Count > 0)
                claimedHost = await hostRepository.ClaimAsync([.. freshHost.Select(finding => finding.FindingId).Distinct()], now, ct).ConfigureAwait(false);
        }
        catch
        {
            if (claimed.Count > 0) await repository.ReleaseAsync(claimed, now, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (claimed.Count == 0 && claimedHost.Count == 0) return 0;
        var told = fresh.FindAll(match => claimed.Contains(match.MatchId));
        var toldHost = freshHost.FindAll(finding => claimedHost.Contains(finding.FindingId));
        try
        {
            await notifications.SendEventAsync(
                NotificationEventTypes.CveDetected, CveWatchAnnouncementPayload.Build(told, toldHost, digest: true), ct).ConfigureAwait(false);
        }
        catch
        {
            // Not sent: the new ones are new again tomorrow night. Cancellation takes this path too.
            if (claimed.Count > 0) await repository.ReleaseAsync(claimed, now, CancellationToken.None).ConfigureAwait(false);
            if (claimedHost.Count > 0) await hostRepository.ReleaseAsync(claimedHost, now, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        logger.LogInformation("CVE watch digest: {New} new matches and {Host} new host findings told", told.Count, claimedHost.Count);
        return told.Count + claimedHost.Count;
    }

    private async Task<List<CveWatchAnnouncedMatch>> ReadOpenAsync(CancellationToken ct)
    {
        var open = new List<CveWatchAnnouncedMatch>();
        var afterProjectId = 0;
        while (await repository.GetOpenProjectIdsAsync(afterProjectId, ProjectChunk, ct).ConfigureAwait(false) is { Count: > 0 } projects)
        {
            foreach (var projectId in projects)
                open.AddRange(await repository.GetOpenAsync(projectId, ct).ConfigureAwait(false));
            afterProjectId = projects[^1];
        }

        return open;
    }
}
