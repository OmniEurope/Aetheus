// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;

namespace Aetheus.Shared.Components.Projects;

/// <summary>A resource named by kind, name and scope (the Compose project, host or path it belongs to).</summary>
public sealed record ProjectServerResourceRef(ProjectServerResourceKind Kind, string Name, string Scope);

/// <summary>
/// Recette R-97 (ADR-058): what the agent of one server is asked about a project, sent base64-encoded in
/// <see cref="EnvironmentVariable"/> of a <c>ProjectResourcesDiscover</c> or <c>ProjectResourcesRemove</c> task
/// whose target is the project id. The identity (Compose projects, hosts, state directories, backup
/// policies, blue-green upstream files) bounds what the agent looks at; <see cref="Remove"/>, for a removal only, is the exact list the
/// user confirmed, and the agent removes nothing outside both.
/// </summary>
public sealed record ProjectServerResourceSpec
{
    public const string EnvironmentVariable = "AETHEUS_PROJECT_RESOURCES_B64";

    private const int MaxIdentityValues = 64;
    private const int MaxRemovals = 2048;

    /// <summary>The project's slug: a state directory must be <c>/var/lib/aetheus-agent/projects/&lt;slug&gt;[-...]</c> or, for a
    /// project created before R-97, <c>/var/lib/&lt;slug&gt;[-...]</c>.</summary>
    public string Slug { get; init; } = string.Empty;

    public List<string> ComposeProjects { get; init; } = [];
    public List<string> Hosts { get; init; } = [];
    public List<string> StateDirectories { get; init; } = [];
    public List<int> BackupPolicyIds { get; init; } = [];

    /// <summary>The blue-green upstream files (<c>UPSTREAM_CONF</c>): files of <c>/etc/apache2/sites-available</c>
    /// named after the slug.</summary>
    public List<string> UpstreamConfigurations { get; init; } = [];
    public List<ProjectServerResourceRef> Remove { get; init; } = [];

    public string Encode() => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, JsonSerializerOptions.Web)));

    /// <summary>The spec of an encoded value, or null when it is not one or fails <see cref="Validate"/>.</summary>
    public static ProjectServerResourceSpec? Decode(string? encoded, out string? refusal)
    {
        refusal = "The project resources specification is missing or unreadable.";
        if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > 1_000_000) return null;
        ProjectServerResourceSpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<ProjectServerResourceSpec>(Convert.FromBase64String(encoded), JsonSerializerOptions.Web);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
        if (spec is null) return null;
        refusal = spec.Validate();
        return refusal is null ? spec : null;
    }

    /// <summary>Null when every value is one a deletion may act on; otherwise the first refusal.</summary>
    public string? Validate()
    {
        var shape = ValidateShape();
        if (shape is not null) return shape;
        if (!ProjectServerResourceNames.IsProjectSlug(Slug))
            return "The project resources specification has no valid project slug.";
        // An explicit test per element: a null element is refused, never taken for "nothing found".
        var refused = Refused(ComposeProjects, value => ProjectServerResourceNames.IsComposeProjectOf(value, Slug))
            ?? Refused(Hosts, ProjectServerResourceNames.IsProjectHost)
            ?? Refused(StateDirectories, value => ProjectServerResourceNames.IsStateDirectoryOf(value, Slug))
            ?? Refused(UpstreamConfigurations, value => ProjectServerResourceNames.IsUpstreamConfigurationOf(value, Slug));
        if (refused is not null)
            return $"Refused value in the project resources specification: '{refused}'.";
        if (BackupPolicyIds.Any(id => !ProjectServerResourceNames.IsBackupPolicyId(id)))
            return "Refused backup policy id in the project resources specification.";
        if (Remove.Any(item => item is null))
            return "A resource to remove is missing in the project resources specification.";
        var foreign = Remove.Find(item => !Covers(item));
        return foreign is null ? null : $"A resource to remove is outside the project's identity: '{foreign.Name}'.";
    }

    /// <summary>The first value that fails <paramref name="valid"/>, shown as <c>(null)</c> when it is null.</summary>
    private static string? Refused(IEnumerable<string?> values, Func<string?, bool> valid)
    {
        foreach (var value in values)
            if (value is null || !valid(value)) return value ?? "(null)";
        return null;
    }

    /// <summary>Null when every list is present and within its bound.</summary>
    private string? ValidateShape()
    {
        if (ComposeProjects is null || Hosts is null || StateDirectories is null || BackupPolicyIds is null || Remove is null
            || UpstreamConfigurations is null)
            return "The project resources specification is incomplete.";
        var identityCounts = new[]
        {
            ComposeProjects.Count, Hosts.Count, StateDirectories.Count, BackupPolicyIds.Count, UpstreamConfigurations.Count
        };
        return identityCounts.Any(count => count > MaxIdentityValues) || Remove.Count > MaxRemovals
            ? "The project resources specification is too large."
            : null;
    }

    /// <summary>Whether a resource is well formed and its scope belongs to this identity.</summary>
    public bool Covers(ProjectServerResourceRef item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!ProjectServerResourceNames.IsResource(item.Kind, item.Name, item.Scope)) return false;
        return item.Kind switch
        {
            ProjectServerResourceKind.Container or ProjectServerResourceKind.Volume or ProjectServerResourceKind.Network
                or ProjectServerResourceKind.Image or ProjectServerResourceKind.DeploymentDirectory =>
                ComposeProjects.Contains(item.Scope, StringComparer.Ordinal),
            ProjectServerResourceKind.UpstreamConfiguration => UpstreamConfigurations.Contains(item.Scope, StringComparer.Ordinal),
            ProjectServerResourceKind.VirtualHost or ProjectServerResourceKind.Certificate =>
                Hosts.Contains(item.Scope, StringComparer.Ordinal),
            ProjectServerResourceKind.StateDirectory => StateDirectories.Contains(item.Scope, StringComparer.Ordinal),
            ProjectServerResourceKind.BackupArchives =>
                BackupPolicyIds.Any(id => ProjectServerResourceNames.BackupArchiveDirectory(id) == item.Scope),
            _ => false
        };
    }
}
