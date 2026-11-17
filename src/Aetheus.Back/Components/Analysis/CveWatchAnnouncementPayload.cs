// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// The payload of an <c>analysis.cve.detected</c> event (recette R-26): ONE event for all the projects, never one per
/// project. <c>Projects</c> lists each project that has something to say, the worst first; the notification module
/// narrows it to the projects a recipient follows when it writes that recipient's mail. <c>Digest</c> is false for the
/// immediate mail of a new severe finding and true for the nightly mail that lists everything still open.
/// R-22: the CVE of a server's host system belong to the server, never to a project: they are told in <c>Servers</c>, one
/// entry per server with its distinct count and the projects it is attached to (the notification module narrows that list
/// to the followed projects like the shared groups), and a project block carries only its dependency vulnerabilities.
/// R-8: a vulnerability is told once, by its identity (CVE or advisory id and package). One that several projects share is
/// not repeated in each project block: it is in <c>Shared</c>, a group per set of <c>ProjectIds</c>, so the counts never
/// count it twice, and a project block holds only what is specific to it. A group keeps its full set of projects: when the
/// notification module narrows the payload to the projects a recipient follows, a group left with a single followed
/// project is read as specific to it.
/// </summary>
internal static class CveWatchAnnouncementPayload
{
    /// <summary>The vulnerabilities a block lists one by one; the count says how many there are in all.</summary>
    internal const int ListedVulnerabilities = 10;

    public static object Build(IReadOnlyCollection<CveWatchAnnouncedMatch> matches, bool digest) =>
        Build(matches, [], digest);

    public static object Build(
        IReadOnlyCollection<CveWatchAnnouncedMatch> matches, IReadOnlyCollection<CveWatchAnnouncedHostFinding> hostFindings, bool digest)
    {
        var projects = matches.Select(match => (match.ProjectId, match.OrganizationId, match.ProjectName))
            .Concat(hostFindings.Select(finding => (finding.ProjectId, finding.OrganizationId, finding.ProjectName)))
            .DistinctBy(project => project.ProjectId)
            .ToDictionary(project => project.ProjectId);

        // One entry per identity, with every project that has it.
        var packages = matches
            .GroupBy(match => (match.RuleId, match.PackageName))
            .Select(group =>
            {
                var first = group.OrderBy(match => match.Version, StringComparer.Ordinal).First();
                return new Entry(
                    [.. group.Select(match => match.ProjectId).Distinct().Order()],
                    first.RuleId, first.PackageName, first.Version,
                    group.Select(match => match.FixedVersion).FirstOrDefault(fix => fix is not null),
                    group.Max(match => match.Severity),
                    group.Any(match => match.Exploited),
                    group.Any(match => match.IsNew));
            });

        var buckets = packages
            .GroupBy(entry => string.Join(',', entry.ProjectIds))
            .Select(group => new Bucket(group.First().ProjectIds, [.. group]))
            .ToList();

        var specific = buckets.Where(bucket => bucket.ProjectIds.Count == 1).ToDictionary(bucket => bucket.ProjectIds[0]);
        var overall = projects.Keys.ToDictionary(id => id, id => new Bucket(
            [id],
            [.. buckets.Where(bucket => bucket.ProjectIds.Contains(id)).SelectMany(bucket => bucket.Packages)]));

        return new
        {
            Digest = digest,
            Projects = projects.Values
                .OrderByDescending(project => overall[project.ProjectId].Exploited)
                .ThenByDescending(project => overall[project.ProjectId].Worst)
                .ThenBy(project => project.ProjectName, StringComparer.Ordinal)
                .Select(project => Project(project.ProjectId, project.OrganizationId, project.ProjectName,
                    specific.GetValueOrDefault(project.ProjectId) ?? new Bucket([project.ProjectId], [])))
                .ToList(),
            Shared = buckets.Where(bucket => bucket.ProjectIds.Count > 1)
                .OrderByDescending(bucket => bucket.Exploited)
                .ThenByDescending(bucket => bucket.Worst)
                .ThenByDescending(bucket => bucket.Count)
                .ThenBy(bucket => string.Join(',', bucket.ProjectIds), StringComparer.Ordinal)
                .Select(Shared)
                .ToList(),
            Servers = Servers(hostFindings)
        };
    }

    private static object Project(int projectId, int organizationId, string projectName, Bucket bucket) => new
    {
        ProjectId = projectId,
        OrganizationId = organizationId,
        ProjectName = projectName,
        Severity = bucket.Count == 0 ? null : bucket.Worst.ToString(),
        VulnerabilityCount = bucket.Packages.Count,
        ExploitedCount = bucket.Exploited,
        NewCount = bucket.New,
        Vulnerabilities = Listed(bucket.Packages)
    };

    private static object Shared(Bucket bucket) => new
    {
        bucket.ProjectIds,
        Severity = bucket.Worst.ToString(),
        VulnerabilityCount = bucket.Packages.Count,
        ExploitedCount = bucket.Exploited,
        NewCount = bucket.New,
        Vulnerabilities = Listed(bucket.Packages)
    };

    /// <summary>One entry per server with open host findings: its distinct vulnerabilities (a finding told for several
    /// projects through the server counts once), the worst severity and the projects it is attached to.</summary>
    private static List<object> Servers(IReadOnlyCollection<CveWatchAnnouncedHostFinding> hostFindings) =>
        [.. hostFindings
            .GroupBy(finding => (finding.ServerId, finding.ServerName))
            .OrderBy(server => server.Key.ServerName, StringComparer.Ordinal)
            .ThenBy(server => server.Key.ServerId)
            .Select(server =>
            {
                var distinct = server.GroupBy(finding => (finding.RuleId, finding.PackageName, finding.Version)).ToList();
                return (object)new
                {
                    server.Key.ServerId,
                    server.Key.ServerName,
                    ProjectIds = server.Select(finding => finding.ProjectId).Distinct().Order().ToList(),
                    Severity = server.Max(finding => finding.Severity).ToString(),
                    VulnerabilityCount = distinct.Count,
                    ExploitedCount = distinct.Count(group => group.Any(finding => finding.Exploited)),
                    NewCount = distinct.Count(group => group.Any(finding => finding.IsNew))
                };
            })];

    /// <summary>The worst first, the exploited before the others of the same severity; the rest only counted.</summary>
    private static List<object> Listed(IEnumerable<Entry> entries) =>
        [.. entries
            .OrderByDescending(entry => entry.Exploited)
            .ThenByDescending(entry => entry.Severity)
            .ThenBy(entry => entry.RuleId, StringComparer.Ordinal)
            .ThenBy(entry => entry.PackageName, StringComparer.Ordinal)
            .Take(ListedVulnerabilities)
            .Select(entry => (object)new { entry.RuleId, entry.PackageName, entry.Version, entry.FixedVersion, Severity = entry.Severity.ToString(), entry.Exploited, entry.IsNew })];

    private sealed record Entry(
        List<int> ProjectIds, string RuleId, string PackageName, string Version, string? FixedVersion,
        AnalysisSeverity Severity, bool Exploited, bool IsNew);

    private sealed record Bucket(List<int> ProjectIds, List<Entry> Packages)
    {
        public int Count => Packages.Count;
        public int Exploited => Packages.Count(entry => entry.Exploited);
        public int New => Packages.Count(entry => entry.IsNew);
        public AnalysisSeverity Worst => Packages.Select(entry => entry.Severity).DefaultIfEmpty().Max();
    }
}
