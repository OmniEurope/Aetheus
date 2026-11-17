// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Services.DomainEvents;
using Aetheus.Shared.Components.Notifications;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// After each matching pass (and after the publication of its findings, which is registered first), tells at once
/// what is both NEW and severe: exploited in the wild (KEV) or Critical (recette R-25). Everything the pass found
/// goes out in ONE <c>analysis.cve.detected</c> event for all the projects (recette R-26); each recipient reads the
/// projects they follow. A published High does not leave here: it waits for the nightly mail
/// (<see cref="CveWatchDigest"/>). A match is announced once: <c>NotifiedAt</c> is set before the event leaves,
/// and cleared again if sending fails. A match that comes back after a fix has its <c>NotifiedAt</c> cleared by the
/// matching, so it is announced again; one that stays open is never repeated. Nothing is carried from one pass to
/// the next, so a pass that was missed is made up by the next.
/// </summary>
internal sealed class CveWatchAnnouncement(
    CveWatchAnnouncementRepository repository,
    INotificationService notifications,
    TimeProvider timeProvider,
    ILogger<CveWatchAnnouncement> logger) : IDomainEventHandler<VulnerabilityMatchesChangedEvent>
{
    public async Task HandleAsync(VulnerabilityMatchesChangedEvent domainEvent, CancellationToken ct = default)
    {
        try
        {
            var announced = await AnnounceAsync(ct).ConfigureAwait(false);
            if (announced > 0)
                logger.LogInformation("CVE watch announcement: {Matches} new severe matches announced at once", announced);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "CVE watch announcement failed; the new severe matches stay pending");
        }
    }

    private async Task<int> AnnounceAsync(CancellationToken ct)
    {
        var found = await repository.GetNewSevereAsync(ct).ConfigureAwait(false);
        if (found.Count == 0) return 0;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var claimed = (await repository.ClaimAsync([.. found.Select(match => match.MatchId)], now, ct).ConfigureAwait(false)).ToHashSet();
        var matches = found.FindAll(match => claimed.Contains(match.MatchId));
        if (matches.Count == 0) return 0;
        try
        {
            await notifications.SendEventAsync(
                NotificationEventTypes.CveDetected, CveWatchAnnouncementPayload.Build(matches, digest: false), ct).ConfigureAwait(false);
        }
        catch
        {
            // Not sent: the next pass finds them again. Cancellation takes this path too.
            await repository.ReleaseAsync([.. claimed], now, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return matches.Count;
    }
}
