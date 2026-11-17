// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Inventory;

internal sealed record InventoryRepositoryTarget(int Id, string DefaultBranch, bool IsEmpty);

/// <summary>What one project has to read. Its repositories are the source ones: the repository of
/// system configuration files (ADR-057) holds nothing the project ships.</summary>
internal sealed record ProjectInventoryTarget(
    int ProjectId, int OrganizationId, string? RepositoryUrl, IReadOnlyList<InventoryRepositoryTarget> Repositories, string? ReleaseCommit);

internal sealed record KnownInventory(int Id, ProjectInventoryOrigin Origin, int? RepositoryId, string? Fingerprint);

/// <summary>One read to store. A null <see cref="Result"/> means the same content: only the read date moves.</summary>
internal sealed record ProjectInventoryDraft(
    int ProjectId, int OrganizationId, int? RepositoryId, ProjectInventoryOrigin Origin,
    string? Ref, string? CommitSha, string? Fingerprint, InventoryBuildResult? Result, DateTime ReadAt);

/// <summary>An image as a server lists it: repository and tag, as <c>docker images</c> prints them.</summary>
internal sealed record ServerImage(string Repository, string Tag);

/// <summary>The servers a project is linked to through <c>ProjectServer</c>, and what each one gives the project.</summary>
/// <param name="Servers">The registered servers linked to this project alone: their images are the project's.</param>
/// <param name="SharedServers">Registered servers linked to another live project as well.</param>
/// <param name="UnregisteredLinks">Links that name no registered server (an external host).</param>
internal sealed record ProjectServerSnapshot(IReadOnlyList<ServerImages> Servers, int SharedServers, int UnregisteredLinks)
{
    public bool HasLinks => Servers.Count + SharedServers + UnregisteredLinks > 0;
}

internal sealed record ServerImages(int ServerId, string ServerName, IReadOnlyList<ServerImage> Images);

internal interface IProjectInventoryRepository
{
    /// <summary>The servers of the project and their images. The link is <c>ProjectServer</c> and nothing else.</summary>
    Task<ProjectServerSnapshot> GetServerSnapshotAsync(int projectId, CancellationToken ct);

    /// <summary>The live projects with no inventory yet, or one read before <paramref name="cutoff"/>.</summary>
    Task<IReadOnlyList<int>> GetDueProjectIdsAsync(DateTime cutoff, CancellationToken ct);

    Task<ProjectInventoryTarget?> GetTargetAsync(int projectId, CancellationToken ct);
    Task<IReadOnlyList<KnownInventory>> GetKnownAsync(int projectId, CancellationToken ct);

    /// <summary>The components of the last run that published one, this project and organization only.</summary>
    Task<SbomSnapshot?> GetLatestSbomAsync(int projectId, int organizationId, int maxComponents, CancellationToken ct);

    Task SaveAsync(ProjectInventoryDraft draft, CancellationToken ct);
    Task RemoveAsync(int projectId, IReadOnlyCollection<int> inventoryIds, CancellationToken ct);
}

/// <summary>
/// Reads the shared Release and Analysis entities itself: those modules sit above this one and tell it
/// of a change through <see cref="IProjectInventoryTrigger"/>. Items are diffed, not replaced, so their
/// identifiers survive for the matches of lot 3.
/// </summary>
internal sealed class ProjectInventoryRepository(AppDbContext db) : IProjectInventoryRepository
{
    private static readonly string[] NotPackages = ["file", "operating-system"];

    public async Task<IReadOnlyList<int>> GetDueProjectIdsAsync(DateTime cutoff, CancellationToken ct) =>
        await db.Projects.AsNoTracking()
            .Where(project => project.DeletionRequestedAt == null)
            .Where(project => !db.ProjectInventories.Any(inventory => inventory.ProjectId == project.Id)
                || db.ProjectInventories.Any(inventory => inventory.ProjectId == project.Id && inventory.ReadAt < cutoff))
            .OrderBy(project => project.Id)
            .Select(project => project.Id)
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<ProjectInventoryTarget?> GetTargetAsync(int projectId, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking()
            .Where(item => item.Id == projectId && item.DeletionRequestedAt == null)
            .Select(item => new { item.OrganizationId, item.RepositoryUrl })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (project is null) return null;

        var repositories = await db.GitInternalRepos.AsNoTracking()
            .Where(repository => repository.ProjectId == projectId && !repository.IsConfiguration)
            .OrderBy(repository => repository.Id)
            .Select(repository => new InventoryRepositoryTarget(repository.Id, repository.DefaultBranch, repository.IsEmpty))
            .ToListAsync(ct).ConfigureAwait(false);
        var releaseCommit = await db.Releases.AsNoTracking()
            .Where(release => release.ProjectId == projectId && release.CommitHash != null
                && release.PublishedAt != null && release.RolledBackAt == null && release.ArchivedAt == null)
            .OrderByDescending(release => release.PublishedAt)
            .Select(release => release.CommitHash)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return new ProjectInventoryTarget(projectId, project.OrganizationId, project.RepositoryUrl, repositories, releaseCommit);
    }

    public async Task<ProjectServerSnapshot> GetServerSnapshotAsync(int projectId, CancellationToken ct)
    {
        var links = await db.ProjectServers.AsNoTracking()
            .Where(link => link.ProjectId == projectId)
            .Select(link => link.ServerId)
            .ToListAsync(ct).ConfigureAwait(false);
        var serverIds = links.OfType<int>().Distinct().ToList();
        // Shared with another project that still exists: the host says nothing of which one runs which image.
        var shared = await db.ProjectServers.AsNoTracking()
            .Where(link => link.ProjectId != projectId && link.ServerId != null && serverIds.Contains(link.ServerId.Value)
                && link.Project.DeletionRequestedAt == null)
            .Select(link => link.ServerId!.Value)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var own = serverIds.Except(shared).ToList();
        var servers = await db.Servers.AsNoTracking()
            .Where(server => own.Contains(server.Id))
            .OrderBy(server => server.Id)
            .Select(server => new { server.Id, server.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var images = (await db.DockerImages.AsNoTracking()
            .Where(image => own.Contains(image.ServerId))
            .OrderBy(image => image.Id)
            .Select(image => new { image.ServerId, image.Repository, image.Tag })
            .ToListAsync(ct).ConfigureAwait(false)).ToLookup(image => image.ServerId);
        return new ProjectServerSnapshot(
            [.. servers.Select(server => new ServerImages(
                server.Id, server.Name, [.. images[server.Id].Select(image => new ServerImage(image.Repository, image.Tag))]))],
            shared.Count,
            links.Count(serverId => serverId is null));
    }

    public async Task<IReadOnlyList<KnownInventory>> GetKnownAsync(int projectId, CancellationToken ct) =>
        await db.ProjectInventories.AsNoTracking()
            .Where(inventory => inventory.ProjectId == projectId)
            .Select(inventory => new KnownInventory(inventory.Id, inventory.Origin, inventory.GitInternalRepoId, inventory.SourceFingerprint))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<SbomSnapshot?> GetLatestSbomAsync(int projectId, int organizationId, int maxComponents, CancellationToken ct)
    {
        var sbomReports = db.AnalysisReports.AsNoTracking().Where(report => report.ProjectId == projectId
            && report.OrganizationId == organizationId && report.Category == AnalysisCategory.Sbom);
        var latest = await sbomReports
            .Where(report => report.Components.Any())
            .OrderByDescending(report => report.CompletedAt)
            .ThenByDescending(report => report.Id)
            .Select(report => new { report.Id, report.PipelineRunId, report.CommitHash, report.BranchName })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (latest is null) return null;

        // A run may publish one SBOM per image: together they are what this run shipped.
        var reports = await sbomReports
            .Where(report => report.Id == latest.Id || (latest.PipelineRunId != null && report.PipelineRunId == latest.PipelineRunId))
            .Select(report => new { report.Id, report.IsTruncated })
            .ToListAsync(ct).ConfigureAwait(false);
        var reportIds = reports.Select(report => report.Id).Order().ToList();
        // Syft lists every file of the image and the system itself: 5,545 of 5,720 rows on Aetheus, none a package.
        var components = await db.AnalysisComponents.AsNoTracking()
            .Where(component => reportIds.Contains(component.AnalysisReportId)
                && component.ProjectId == projectId && component.OrganizationId == organizationId)
            .Where(component => component.ComponentType == null
                || !NotPackages.Contains(component.ComponentType.ToLower()))
            .OrderBy(component => component.Id)
            .Take(maxComponents + 1)
            .Select(component => new SbomComponent(component.Name, component.Version, component.PackageUrl, component.IsDirect))
            .ToListAsync(ct).ConfigureAwait(false);
        return new SbomSnapshot(reportIds, latest.CommitHash, latest.BranchName, reports.Any(report => report.IsTruncated), components);
    }

    public async Task SaveAsync(ProjectInventoryDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var inventory = await db.ProjectInventories.FirstOrDefaultAsync(item => item.ProjectId == draft.ProjectId
            && item.Origin == draft.Origin && item.GitInternalRepoId == draft.RepositoryId, ct).ConfigureAwait(false);
        if (inventory is null)
        {
            inventory = new ProjectInventory
            {
                OrganizationId = draft.OrganizationId,
                ProjectId = draft.ProjectId,
                GitInternalRepoId = draft.RepositoryId,
                Origin = draft.Origin,
                ChangedAt = draft.ReadAt
            };
            db.ProjectInventories.Add(inventory);
        }

        inventory.Ref = draft.Ref;
        inventory.CommitSha = draft.CommitSha;
        inventory.ReadAt = draft.ReadAt;
        if (draft.Result is { } result)
        {
            inventory.SourceFingerprint = draft.Fingerprint;
            inventory.Coverage = result.Coverage;
            inventory.Reason = result.Reason;
            inventory.ItemCount = result.Items.Count;
            await ApplyItemsAsync(inventory, draft, result, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task ApplyItemsAsync(ProjectInventory inventory, ProjectInventoryDraft draft, InventoryBuildResult result, CancellationToken ct)
    {
        var stored = inventory.Id == 0
            ? []
            : await db.ProjectInventoryItems.Where(item => item.ProjectInventoryId == inventory.Id).ToListAsync(ct).ConfigureAwait(false);
        var byKey = stored.ToDictionary(item => (item.Ecosystem, item.PackageName, item.Version, item.Kind));
        var added = 0;
        foreach (var item in result.Items)
        {
            if (byKey.Remove((item.Ecosystem, item.PackageName, item.Version, item.Kind), out var existing))
            {
                existing.IsDirect = item.IsDirect;
                existing.SourcePath = item.SourcePath;
                existing.PackageUrl = item.PackageUrl;
                continue;
            }

            added++;
            inventory.Items.Add(new ProjectInventoryItem
            {
                OrganizationId = draft.OrganizationId,
                ProjectId = draft.ProjectId,
                Ecosystem = item.Ecosystem,
                PackageName = item.PackageName,
                Version = item.Version,
                PackageUrl = item.PackageUrl,
                IsDirect = item.IsDirect,
                SourcePath = item.SourcePath,
                Kind = item.Kind
            });
        }

        db.ProjectInventoryItems.RemoveRange(byKey.Values);
        if (added > 0 || byKey.Count > 0) inventory.ChangedAt = draft.ReadAt;
    }

    public async Task RemoveAsync(int projectId, IReadOnlyCollection<int> inventoryIds, CancellationToken ct)
    {
        if (inventoryIds.Count == 0) return;
        var stale = await db.ProjectInventories
            .Where(inventory => inventory.ProjectId == projectId && inventoryIds.Contains(inventory.Id))
            .ToListAsync(ct).ConfigureAwait(false);
        db.ProjectInventories.RemoveRange(stale);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
