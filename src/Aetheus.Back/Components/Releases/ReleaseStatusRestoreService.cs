// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Aetheus.Back.Components.Releases;

public sealed class ReleaseStatusRestoreService(
    IReleaseRepository repo,
    IReleaseService releases,
    IHubContext<ReleaseHub> releaseHub,
    IAuditService audit) : IReleaseStatusRestoreService
{
    public async Task<ReleaseDto> RestoreAsync(int releaseId, RestoreReleaseStatusRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = request.Reason.Trim();
        if (reason.Length == 0)
            throw new BadRequestException("A reason is required to restore a release status.");

        var release = await repo.FindReleaseAsync(releaseId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Release {releaseId} not found.");
        var from = release.Status;
        if (!ReleaseStatusRestore.CanRestoreFrom(from))
            throw new BadRequestException("An archived release cannot have its status restored; publish it again instead.");
        if (!ReleaseStatusRestore.Targets.Contains(request.Status))
            throw new BadRequestException($"A release status can only be restored to {string.Join(", ", ReleaseStatusRestore.Targets)}.");
        if (from == request.Status)
            throw new BadRequestException($"The release is already {from}.");

        List<Release> demoted = [];
        if (request.Status == ReleaseStatus.Deployed)
        {
            demoted = (await repo.GetDeployedProjectReleasesAsync(release.ProjectId, ct).ConfigureAwait(false))
                .Where(other => other.Id != release.Id)
                .ToList();
            foreach (var other in demoted)
                other.Status = ReleaseStatus.Superseded;
        }
        release.Status = request.Status;
        // One save: the demotion and the restored status land together or not at all.
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        var demotedText = demoted.Count == 0 ? "none" : string.Join(",", demoted.Select(other => other.Id));
        await audit.LogAsync("ReleaseStatusRestored", "Release", release.Id,
            $"version={release.Version}; from={from}; to={request.Status}; superseded={demotedText}; reason={reason}", ct).ConfigureAwait(false);

        foreach (var other in demoted)
            await BroadcastAsync(other.Id, ct).ConfigureAwait(false);
        return await BroadcastAsync(release.Id, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Release {releaseId} not found.");
    }

    private async Task<ReleaseDto?> BroadcastAsync(int releaseId, CancellationToken ct)
    {
        var dto = await releases.GetReleaseAsync(releaseId, ct).ConfigureAwait(false);
        if (dto is null) return null;
        await releaseHub.Clients.Groups([HubGroups.AllReleases, HubGroups.ProjectReleases(dto.ProjectId)])
            .SendAsync("ReleaseStatusChanged", dto, ct).ConfigureAwait(false);
        return dto;
    }
}
