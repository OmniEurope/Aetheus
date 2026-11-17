// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aetheus.Back.Components.Notifications;

/// <summary>
/// An event that tells about several projects at once (recette R-26): its payload has no <c>ProjectId</c> of its own
/// but a <c>Projects</c> array whose items each carry one. The recipients are the followers of any of those projects,
/// and each one is given an event narrowed to the projects they follow and can read, so one mail covers them all
/// and never names a project the reader cannot see.
/// </summary>
internal static class ProjectDigestPayload
{
    private const string ProjectsProperty = "Projects";
    private const string SharedProperty = "Shared";
    private const string ServersProperty = "Servers";

    /// <summary>The distinct project identifiers of the <c>Projects</c> array; empty when the payload has none.</summary>
    public static IReadOnlyList<int> ProjectIds(string jsonPayload) =>
        [.. (Read(jsonPayload) ?? []).Select(ProjectIdOf).OfType<int>().Distinct()];

    /// <summary>The same payload with only the projects of <paramref name="allowed"/>; null when none is left.</summary>
    public static string? Narrow(string jsonPayload, IReadOnlySet<int> allowed)
    {
        try
        {
            if (JsonNode.Parse(jsonPayload) is not JsonObject root || root[ProjectsProperty] is not JsonArray projects) return null;
            var kept = new JsonArray();
            foreach (var project in projects.OfType<JsonObject>().Where(item => ProjectIdOf(item) is { } id && allowed.Contains(id)))
                kept.Add(project.DeepClone());
            if (kept.Count == 0) return null;
            root[ProjectsProperty] = kept;
            // R-8: what several projects share keeps only the projects the recipient follows; a group left with none is dropped.
            NarrowGroups(root, SharedProperty, allowed);
            // R-22: the servers' host vulnerabilities are told per server, narrowed the same way.
            NarrowGroups(root, ServersProperty, allowed);
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            // A payload that is not JSON names no project.
            return null;
        }
    }

    private static void NarrowGroups(JsonObject root, string property, IReadOnlySet<int> allowed)
    {
        if (root[property] is not JsonArray groups) return;
        var keptGroups = new JsonArray();
        foreach (var group in groups.OfType<JsonObject>())
        {
            if (group["ProjectIds"] is not JsonArray ids) continue;
            var followed = new JsonArray();
            foreach (var id in ids.OfType<JsonValue>().Where(id => id.TryGetValue<int>(out var value) && allowed.Contains(value)))
                followed.Add(id.DeepClone());
            if (followed.Count == 0) continue;
            var narrowedGroup = (JsonObject)group.DeepClone();
            narrowedGroup["ProjectIds"] = followed;
            keptGroups.Add(narrowedGroup);
        }
        root[property] = keptGroups;
    }

    /// <summary>The organization all the projects of the payload belong to; null when they belong to several.</summary>
    public static int? SingleOrganizationId(string jsonPayload)
    {
        var organizations = (Read(jsonPayload) ?? [])
            .Select(project => project["OrganizationId"] is JsonValue value && value.TryGetValue<int>(out var id) ? id : (int?)null)
            .Distinct()
            .ToList();
        return organizations is [{ } only] ? only : null;
    }

    private static List<JsonObject>? Read(string jsonPayload)
    {
        if (string.IsNullOrWhiteSpace(jsonPayload)) return null;
        try
        {
            return JsonNode.Parse(jsonPayload) is JsonObject root && root[ProjectsProperty] is JsonArray projects
                ? [.. projects.OfType<JsonObject>()]
                : null;
        }
        catch (JsonException)
        {
            // A payload that is not JSON names no project.
            return null;
        }
    }

    private static int? ProjectIdOf(JsonObject project) =>
        project["ProjectId"] is JsonValue value && value.TryGetValue<int>(out var id) ? id : null;
}
