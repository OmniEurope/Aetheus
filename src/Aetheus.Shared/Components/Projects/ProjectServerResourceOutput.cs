// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Projects;

/// <summary>One resource line of a project resources task.</summary>
public sealed record ProjectServerResourceLine(
    ProjectServerResourceKind Kind, string Name, string Scope, ProjectServerResourceOutcome Outcome, string? Reason);

/// <summary>The parsed output of a project resources task. <see cref="Complete"/> is false when the agent did not
/// reach its closing line, so a partial read is never taken for a full one.</summary>
public sealed record ProjectServerResourceReport(
    bool Complete, IReadOnlyList<ProjectServerResourceLine> Resources, IReadOnlyList<ProjectServerUnreadableDto> Unreadable);

/// <summary>
/// The machine-readable lines a project resources task prints (ADR-058), one per resource, fields separated by
/// tabs: <c>RESOURCE|REMOVED|ABSENT|FAILED|NOTLISTED kind name scope [reason]</c>, <c>UNREADABLE kind scope
/// reason</c>, then <c>PROJECT-RESOURCES complete</c>. Written by the agent, read by the backend; a line whose
/// names fail <see cref="ProjectServerResourceNames"/> is reported as unreadable, never used.
/// </summary>
public static class ProjectServerResourceOutput
{
    public const string CompleteLine = "PROJECT-RESOURCES\tcomplete";
    private const string UnreadableTag = "UNREADABLE";

    private static readonly Dictionary<ProjectServerResourceOutcome, string> s_tags = new()
    {
        [ProjectServerResourceOutcome.Found] = "RESOURCE",
        [ProjectServerResourceOutcome.Removed] = "REMOVED",
        [ProjectServerResourceOutcome.Absent] = "ABSENT",
        [ProjectServerResourceOutcome.Failed] = "FAILED",
        [ProjectServerResourceOutcome.NotListed] = "NOTLISTED",
    };

    public static string Line(ProjectServerResourceOutcome outcome, ProjectServerResourceRef resource, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var line = $"{s_tags[outcome]}\t{resource.Kind}\t{Clean(resource.Name)}\t{Clean(resource.Scope)}";
        return reason is null ? line : $"{line}\t{Clean(reason)}";
    }

    public static string Unreadable(ProjectServerResourceKind kind, string scope, string reason) =>
        $"{UnreadableTag}\t{kind}\t{Clean(scope)}\t{Clean(reason)}";

    public static ProjectServerResourceReport Parse(string? output)
    {
        var resources = new List<ProjectServerResourceLine>();
        var unreadable = new List<ProjectServerUnreadableDto>();
        var complete = false;
        foreach (var raw in (output ?? string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line == CompleteLine)
            {
                complete = true;
                continue;
            }
            var fields = line.Split('\t');
            if (fields.Length < 4) continue;
            if (fields[0] == UnreadableTag)
            {
                if (TryKind(fields[1], out var unreadableKind))
                    unreadable.Add(new ProjectServerUnreadableDto(unreadableKind, fields[2], fields[3]));
                continue;
            }
            var outcome = s_tags.FirstOrDefault(tag => tag.Value == fields[0]);
            if (outcome.Value is null) continue;
            if (!TryKind(fields[1], out var kind) || !ProjectServerResourceNames.IsResource(kind, fields[2], fields[3]))
            {
                unreadable.Add(new ProjectServerUnreadableDto(
                    TryKind(fields[1], out var named) ? named : ProjectServerResourceKind.Container, fields[3],
                    $"The agent reported a resource whose name is refused: '{fields[2]}'."));
                continue;
            }
            resources.Add(new ProjectServerResourceLine(kind, fields[2], fields[3], outcome.Key, fields.Length > 4 ? fields[4] : null));
        }
        return new ProjectServerResourceReport(complete, resources, unreadable);
    }

    private static bool TryKind(string value, out ProjectServerResourceKind kind) =>
        Enum.TryParse(value, ignoreCase: false, out kind) && Enum.IsDefined(kind) && !int.TryParse(value, out _);

    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
