// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
namespace Aetheus.Back.Components.Projects;

public sealed class ProjectDataRetentionRepository(AppDbContext db) : IProjectDataRetentionRepository
{
    public Task<Project?> FindAsync(int projectId, CancellationToken ct = default) =>
        db.Projects.FirstOrDefaultAsync(project => project.Id == projectId, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
}
