// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Queries of <see cref="IPipelineProjectOwnerReader"/>, moved unchanged from
/// <c>PipelineServerResolver</c>; the <see cref="IPipelineRepository"/> facade delegates to them.</summary>
internal sealed class PipelineProjectOwnerRepository(AppDbContext db) : IPipelineProjectOwnerReader
{
    // Uses the owning organization’s Owner username for Git-synchronized pipelines so automatic
    // runs have an authorizable identity, or null if none can be resolved.
    public async Task<string?> GetProjectOwnerUsernameAsync(int projectId, CancellationToken ct = default)
    {
        var orgId = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => (int?)p.OrganizationId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (orgId is null) return null;
        return await db.OrganizationMembers.AsNoTracking()
            .Where(m => m.OrganizationId == orgId.Value && m.Role == OrganizationRole.Owner)
            .OrderBy(m => m.UserId) // deterministic when an org has more than one Owner
            .Select(m => m.User.Username)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    // F-INF-02b: does this username still map to an active user? A pipeline owner that fails this can never
    // pass the F-EXEC-1b gate, so the sync path treats it as unresolvable and re-resolves a live owner.
    public async Task<bool> IsActiveUsernameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return false;
        return await db.Users.AsNoTracking()
            .AnyAsync(u => u.Username == username && u.IsActive, ct).ConfigureAwait(false);
    }
}
