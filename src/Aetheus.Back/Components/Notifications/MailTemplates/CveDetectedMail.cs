// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;
using System.Text.Json;
using Aetheus.Back.Services.Email;
using static Aetheus.Back.Services.Email.EmailHtmlLayout;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// The part of the <c>analysis.cve.detected</c> mail that is not like the others (recette R-26): one mail for several
/// projects, a block per project with its link, the vulnerabilities still open, those no mail has told yet counted as
/// new. The payload is <c>{ Digest, Projects: [ { ProjectId, ProjectName, Severity, VulnerabilityCount, ExploitedCount,
/// NewCount, Vulnerabilities: [...] } ], Shared: [...], Servers: [ { ServerId, ServerName, ProjectIds, Severity,
/// VulnerabilityCount, ExploitedCount, NewCount } ] }</c>, already narrowed by the notification module to the projects the
/// recipient follows. R-14: a block only says how many distinct vulnerabilities it holds, with a link to the page that
/// lists them, never the list. R-22: the vulnerabilities of a server's host system are told in a section of that server,
/// never in a project's block.
/// </summary>
internal sealed class CveDetectedMail(string language, MailBlockColours colours, string? frontOrigin)
{
    public const string EventType = "analysis.cve.detected";

    /// <summary>Where the CVE watch screen lives (a tab of the quality page, recette R-15). A tab the page does not have yet
    /// falls back to its first one; each project of the mail also links its own findings.</summary>
    internal const string WatchPath = "/analysis?tab=cve-watch";

    private static readonly string[] SeverityOrder = ["Critical", "High"];

    /// <summary>True when the payload lists projects (it always does since recette R-26).</summary>
    public static bool IsList(JsonElement? root) => Projects(root) is { Count: > 0 };

    /// <summary>True for the nightly mail, which lists everything still open; false for the mail of a new severe finding.</summary>
    public static bool IsDigest(JsonElement? root) =>
        root is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("Digest", out var digest) && digest.ValueKind == JsonValueKind.True;

    /// <summary>One project: its own findings. Several: the CVE watch.</summary>
    public static (string Path, string LinkKey) Destination(JsonElement? root) =>
        Projects(root) is [{ } only] && ProjectId(only) is { } id
            ? (FindingsPath(id), "AnalysisLink")
            : (WatchPath, "CveWatchLink");

    /// <summary>The colour follows the worst severity of the whole mail: Critical and exploited are red, High amber.</summary>
    public static string? WorstSeverity(JsonElement? root)
    {
        var blocks = Projects(root) ?? [];
        blocks.AddRange(SharedGroups(root));
        blocks.AddRange(ServerSections(root));
        if (blocks.Any(block => Count(block, "ExploitedCount") > 0)) return "Critical";
        return SeverityOrder.FirstOrDefault(severity => blocks.Any(block => Text(block, "Severity") == severity));
    }

    private static string FindingsPath(int projectId) => $"/projects/{projectId}/quality?tab=findings";

    /// <summary>The quality page of a server, where its host system vulnerabilities are listed.</summary>
    private static string ServerQualityPath(int serverId) => $"/servers/{serverId}/quality";

    /// <summary>One set of projects and what is told about it: the findings specific to one project, or those several share.</summary>
    private sealed class Block(List<int> ids)
    {
        public List<int> Ids { get; } = ids;
        public int Vulnerabilities { get; set; }
        public int Exploited { get; set; }
        public int New { get; set; }
        public string? Severity { get; set; }
        public int Listed { get; set; }
        public int Total => Math.Max(Vulnerabilities, Listed);
    }

    /// <summary>
    /// The summary rows, then the findings common to several projects once, then one row per project with only what is
    /// specific to it (R-8), then one section per server with the vulnerabilities of its host system (R-22). The counts are
    /// of distinct vulnerabilities.
    /// </summary>
    public string Rows(JsonElement? root)
    {
        var projects = Projects(root) ?? [];
        var digest = IsDigest(root);
        var blocks = Blocks(root, projects);
        var servers = ServerSections(root);
        var common = blocks.Where(block => block.Ids.Count > 1).ToList();
        var culture = CultureInfo.GetCultureInfo(language);
        var html = new StringBuilder();
        Row(html, T("CveProjects"), projects.Count.ToString(culture));
        Row(html, T("CveVulnerabilities"), blocks.Sum(block => block.Vulnerabilities).ToString(culture));
        // R-41: the CVE of the host systems of the projects' servers, said apart from the packages.
        if (servers.Sum(server => Count(server, "VulnerabilityCount")) is var hostCount and > 0)
            Row(html, T("CveHostVulnerabilities"), hostCount.ToString(culture));
        if (common.Sum(block => block.Total) is var shared and > 0)
            Row(html, T("CveCommonVulnerabilities"), shared.ToString(culture));
        if (blocks.Sum(block => block.Exploited) + servers.Sum(server => Count(server, "ExploitedCount")) is var exploited and > 0)
            Row(html, T("ExploitedCount"), exploited.ToString(culture));
        if (digest && blocks.Sum(block => block.New) + servers.Sum(server => Count(server, "NewCount")) is var added and > 0)
            Row(html, T("CveNewCount"), added.ToString(culture));
        var names = projects.Where(project => ProjectId(project) is not null)
            .ToDictionary(project => ProjectId(project)!.Value, project => Text(project, "ProjectName") ?? string.Empty);
        foreach (var block in common.OrderByDescending(block => block.Exploited).ThenByDescending(block => Rank(block.Severity)).ThenByDescending(block => block.Total))
            BlockRow(html, block, names);
        foreach (var block in blocks.Where(block => block.Ids.Count == 1 && block.Total > 0))
            BlockRow(html, block, names);
        foreach (var server in servers.Where(server => Count(server, "VulnerabilityCount") > 0))
            ServerRow(html, server);
        return html.ToString();
    }

    /// <summary>The blocks of the payload merged by set of projects, the one-project blocks in the order of the projects. A payload
    /// from before R-8 has no shared groups: every block is then a project's own.</summary>
    private static List<Block> Blocks(JsonElement? root, List<JsonElement> projects)
    {
        var blocks = new List<Block>();
        void Add(List<int> ids, JsonElement source)
        {
            var key = ids.Order().ToList();
            var block = blocks.Find(existing => existing.Ids.SequenceEqual(key));
            if (block is null) blocks.Add(block = new Block(key));
            block.Vulnerabilities += Count(source, "VulnerabilityCount");
            block.Exploited += Count(source, "ExploitedCount");
            block.New += Count(source, "NewCount");
            if (Text(source, "Severity") is { } severity && Rank(severity) > Rank(block.Severity)) block.Severity = severity;
            block.Listed += source.TryGetProperty("Vulnerabilities", out var items) && items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
        }
        foreach (var project in projects)
            if (ProjectId(project) is { } id) Add([id], project);
        foreach (var group in SharedGroups(root))
        {
            var followed = group.TryGetProperty("ProjectIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? ids.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _)).Select(item => item.GetInt32()).Distinct().ToList()
                : [];
            if (followed.Count > 0) Add(followed, group);
        }
        return blocks;
    }

    private static List<JsonElement> SharedGroups(JsonElement? root) => ObjectsOf(root, "Shared");

    private static List<JsonElement> ServerSections(JsonElement? root) => ObjectsOf(root, "Servers");

    private static List<JsonElement> ObjectsOf(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)]
            : [];

    private static int Rank(string? severity) => severity switch { "Critical" => 5, "High" => 4, "Medium" => 3, "Low" => 2, "Info" => 1, _ => 0 };

    private void BlockRow(StringBuilder html, Block block, Dictionary<int, string> names)
    {
        var links = block.Ids.Select(id => ProjectLink(id, names.GetValueOrDefault(id) ?? string.Empty)).ToList();
        var common = block.Ids.Count > 1;
        var label = common
            ? Encode(T("CveCommonLabel")).Replace("{0}", string.Join(", ", links), StringComparison.Ordinal)
            : links[0];
        Detail(html, label, block.Total, common ? WatchPath : FindingsPath(block.Ids[0]));
    }

    /// <summary>One server: its name, how many distinct vulnerabilities its host system has, and its quality page.</summary>
    private void ServerRow(StringBuilder html, JsonElement server)
    {
        var count = Count(server, "VulnerabilityCount");
        var serverId = server.TryGetProperty("ServerId", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var value) && value > 0 ? value : (int?)null;
        Detail(html, Encode(string.Format(CultureInfo.GetCultureInfo(language), T("CveServerLabel"), Text(server, "ServerName"))), count, serverId is { } sid ? ServerQualityPath(sid) : null);
    }

    /// <summary>The row of a block: its label (already encoded), the number of distinct vulnerabilities and a link to the page that lists them.</summary>
    private void Detail(StringBuilder html, string label, int count, string? path)
    {
        var line = Encode(string.Format(CultureInfo.GetCultureInfo(language), T("CveBlockCount"), count));
        var href = path is null ? null : Url(frontOrigin, path);
        var more = href is null ? string.Empty : "<br><a href=\"" + Encode(href) + "\">" + Encode(T("CveMoreInformation")) + "</a>";
        html.Append("<tr><td style=\"width:38%;padding:10px;font-size:13px;color:").Append(colours.Muted).Append("\"><strong>")
            .Append(label).Append("</strong></td><td style=\"padding:10px;font-size:14px;color:").Append(colours.Ink).Append("\">")
            .Append(line).Append(more).Append("</td></tr>");
    }

    private string ProjectLink(int id, string name) =>
        Url(frontOrigin, FindingsPath(id)) is { } href ? "<a href=\"" + Encode(href) + "\">" + Encode(name) + "</a>" : Encode(name);

    private void Row(StringBuilder html, string label, string value) =>
        html.Append("<tr><td style=\"width:38%;padding:10px;font-size:13px;color:").Append(colours.Muted).Append("\">").Append(Encode(label))
            .Append("</td><td style=\"padding:10px;font-size:14px;color:").Append(colours.Ink).Append("\">").Append(Encode(value)).Append("</td></tr>");

    private static List<JsonElement>? Projects(JsonElement? root) =>
        root is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("Projects", out var projects) && projects.ValueKind == JsonValueKind.Array
            ? [.. projects.EnumerateArray().Where(project => project.ValueKind == JsonValueKind.Object)]
            : null;

    private static int? ProjectId(JsonElement project) =>
        project.TryGetProperty("ProjectId", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var value) && value > 0 ? value : null;

    private static int Count(JsonElement project, string name) =>
        project.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var value) ? value : 0;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private string T(string key) => NotificationEmailText.Get(key, language);
}
