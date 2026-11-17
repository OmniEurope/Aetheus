// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// Which projects and servers a recipient of the recap may read (recette R-25, R-41): the per-project and per-server
/// lines of the CVE section name a project or a server, so a recipient is only told about the ones their roles reach.
/// An address with no active account reads none; a site-wide administrator reads all of them.
/// </summary>
internal sealed class DailyRecapRecipientScope(IDailyRecapRepository repo, IResourceAuthorizationService authorization)
{
    /// <summary>The report as this recipient may read it: the same facts, only the projects and servers they may read in the per-project and per-server CVE lines.</summary>
    public async Task<DailyRecapReport> ForAsync(DailyRecapReport report, string address, CancellationToken ct)
    {
        if (report.CveProjects.Count == 0 && report.CveServers.Count == 0) return report;
        var username = await repo.FindActiveUsernameByEmailAsync(address, ct).ConfigureAwait(false);
        var projects = await ReadableIdsAsync(username, ResourceType.Project, report.CveProjects.Count, ct).ConfigureAwait(false);
        var servers = await ReadableIdsAsync(username, ResourceType.Server, report.CveServers.Count, ct).ConfigureAwait(false);
        return report with
        {
            CveProjects = projects is null ? report.CveProjects : [.. report.CveProjects.Where(project => projects.Contains(project.ProjectId))],
            CveServers = servers is null ? report.CveServers : [.. report.CveServers.Where(server => servers.Contains(server.ServerId))]
        };
    }

    /// <summary>Null: every resource of that type. Otherwise the ids the recipient may read (empty for an address nobody holds).</summary>
    private async Task<HashSet<int>?> ReadableIdsAsync(string? username, ResourceType type, int listed, CancellationToken ct)
    {
        if (listed == 0) return null;
        if (username is null) return [];
        var ids = await authorization.GetAccessibleResourceIdsForUsernameAsync(username, type, Permission.Read, ct).ConfigureAwait(false);
        return ids is null ? null : [.. ids];
    }
}
