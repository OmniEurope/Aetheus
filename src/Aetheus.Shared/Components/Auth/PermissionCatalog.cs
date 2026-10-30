// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Auth;

/// <summary>
/// One named permission (R-11).
/// </summary>
/// <param name="Key">The stable key (<see cref="PermissionKeys"/>), stored on the role's grants.</param>
/// <param name="Area">The group the role editor shows it in (<c>pipeline</c>, <c>server</c>, ...).</param>
/// <param name="ResourceType">The resource a grant names by id, and whose organization scopes it; null
/// for a permission held on the organization itself (<see cref="PermissionKeys.OrganizationMembersManage"/>).</param>
/// <param name="Level">For a level key, the Read, Write or Admin level it stands for; null for an action key.</param>
/// <param name="LegacyLevel">For an action key, the level of <paramref name="ResourceType"/> that allowed the
/// action before it had a permission of its own: the migration gives the key to every role holding that
/// level, so no existing role gains or loses the action.</param>
/// <param name="DefaultRoles">The built-in roles holding it on a fresh install (the site Admin holds every key).</param>
public sealed record PermissionDefinition(
    string Key,
    string Area,
    ResourceType? ResourceType,
    Permission? Level,
    Permission? LegacyLevel,
    IReadOnlyList<string> DefaultRoles)
{
    /// <summary>True for a <c>*.view</c>, <c>*.edit</c> or <c>*.manage</c> key.</summary>
    public bool IsLevel => Level is not null;

    /// <summary>The resx key of its label: <c>PermissionKey_{Key}</c>.</summary>
    public string LabelKey => $"PermissionKey_{Key}";
}

/// <summary>
/// The catalogue of named permissions (R-11). Level keys map one-to-one to a (resource type, level) pair
/// of the level model, so a role assembled from keys is honoured by every Read, Write or Admin check; an
/// action key is checked by <c>HasPermissionAsync(user, key, resourceId)</c> on the action it names.
/// </summary>
public static class PermissionCatalog
{
    private static readonly string[] Readers = [RoleNames.Reader, RoleNames.Contributor, RoleNames.OrganizationAdmin];
    private static readonly string[] Writers = [RoleNames.Contributor, RoleNames.OrganizationAdmin];
    private static readonly string[] Admins = [RoleNames.OrganizationAdmin];

    /// <summary>The areas in the order the role editor shows them.</summary>
    public static readonly IReadOnlyList<string> Areas =
        ["project", "pipeline", "release", "environment", "variables", "vault", "server", "connection", "org"];

    /// <summary>Every permission, level keys first within each area.</summary>
    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        .. Levels("project", ResourceType.Project, PermissionKeys.ProjectView, PermissionKeys.ProjectEdit, PermissionKeys.ProjectManage),
        .. Levels("pipeline", ResourceType.Pipeline, PermissionKeys.PipelineView, PermissionKeys.PipelineEdit, PermissionKeys.PipelineManage),
        Action(PermissionKeys.PipelineRun, "pipeline", ResourceType.Pipeline, Permission.Write, Writers),
        Action(PermissionKeys.PipelineApprovalDecide, "pipeline", ResourceType.Pipeline, Permission.Write, Writers),
        .. Levels("pipeline", ResourceType.PipelineTemplate, PermissionKeys.PipelineTemplateView, PermissionKeys.PipelineTemplateEdit, PermissionKeys.PipelineTemplateManage),
        .. Levels("release", ResourceType.Release, PermissionKeys.ReleaseView, PermissionKeys.ReleaseEdit, PermissionKeys.ReleaseManage),
        Action(PermissionKeys.ReleaseRollback, "release", ResourceType.Release, Permission.Write, Writers),
        Action(PermissionKeys.ReleaseStatusRestore, "release", ResourceType.Release, Permission.Write, Writers),
        .. Levels("environment", ResourceType.Environment, PermissionKeys.EnvironmentView, PermissionKeys.EnvironmentEdit, PermissionKeys.EnvironmentManage),
        .. Levels("variables", ResourceType.VariableLibrary, PermissionKeys.VariablesView, PermissionKeys.VariablesEdit, PermissionKeys.VariablesManage),
        .. Levels("vault", ResourceType.Vault, PermissionKeys.VaultView, PermissionKeys.VaultEdit, PermissionKeys.VaultManage),
        Action(PermissionKeys.VaultSecretReveal, "vault", ResourceType.Vault, Permission.Write, Writers),
        .. Levels("server", ResourceType.Server, PermissionKeys.ServerView, PermissionKeys.ServerEdit, PermissionKeys.ServerManage),
        Action(PermissionKeys.ServerServicesControl, "server", ResourceType.Server, Permission.Write, Writers),
        Action(PermissionKeys.ServerFirewallManage, "server", ResourceType.Server, Permission.Write, Admins),
        Action(PermissionKeys.ServerSystemUpgrade, "server", ResourceType.Server, Permission.Write, Admins),
        Action(PermissionKeys.ServerPortsReserve, "server", ResourceType.Server, Permission.Write, Writers),
        // R-11: the environment of a container often carries its secrets. It was readable with Server Read;
        // it is now an Organization Admin's by default (a migrated custom role keeps what it had).
        Action(PermissionKeys.ServerDockerContainerEnvView, "server", ResourceType.Server, Permission.Read, Admins),
        Action(PermissionKeys.ServerMailboxRead, "server", ResourceType.Server, Permission.Read, Readers),
        Action(PermissionKeys.ServerMailboxManage, "server", ResourceType.Server, Permission.Write, Writers),
        .. Levels("connection", ResourceType.ServiceConnection, PermissionKeys.ConnectionView, PermissionKeys.ConnectionEdit, PermissionKeys.ConnectionManage),
        // Held on the organization itself; it replaced the "Organization Admin" role-name check.
        new(PermissionKeys.OrganizationMembersManage, "org", null, null, null, Admins),
        // Held on the organization itself: its own roles, and its own versions of the mail templates.
        new(PermissionKeys.OrganizationRolesManage, "org", null, null, null, Admins),
        new(PermissionKeys.OrganizationMailTemplatesManage, "org", null, null, null, Admins),
    ];

    /// <summary>The keys that open the Administration section to an organization's administrator: each one
    /// is held on an organization and gives one of its pages (users and organizations, roles, mail templates).</summary>
    public static readonly IReadOnlyList<string> OrganizationAdministrationKeys =
    [
        PermissionKeys.OrganizationMembersManage,
        PermissionKeys.OrganizationRolesManage,
        PermissionKeys.OrganizationMailTemplatesManage
    ];

    private static readonly Dictionary<string, PermissionDefinition> ByKey =
        All.ToDictionary(definition => definition.Key, StringComparer.Ordinal);

    /// <summary>The definition of <paramref name="key"/>, or null when the catalogue has no such key.</summary>
    public static PermissionDefinition? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var definition) ? definition : null;

    /// <summary>The definition of <paramref name="key"/>; an unknown key is a programming error.</summary>
    public static PermissionDefinition Get(string key) =>
        Find(key) ?? throw new ArgumentException($"'{key}' is not a permission of the catalogue.", nameof(key));

    /// <summary>The level key of a resource type and level, or null for a type the catalogue does not
    /// offer to organization roles (agent pools and Docker hosts are a site Admin's).</summary>
    public static string? LevelKey(ResourceType resourceType, Permission level) =>
        All.FirstOrDefault(definition => definition.ResourceType == resourceType && definition.Level == level)?.Key;

    /// <summary>The level keys a (type, level) grant amounts to: the level and every lower one.</summary>
    public static IEnumerable<string> LevelKeysUpTo(ResourceType resourceType, Permission level) =>
        All.Where(definition => definition.ResourceType == resourceType && definition.Level <= level)
            .Select(definition => definition.Key);

    /// <summary>
    /// The action keys a level grant gave before those actions had keys of their own: what the migration
    /// adds to a role holding (<paramref name="resourceType"/>, <paramref name="level"/>).
    /// </summary>
    public static IEnumerable<string> ActionKeysOfLegacyLevel(ResourceType resourceType, Permission level) =>
        All.Where(definition => definition.ResourceType == resourceType && definition.LegacyLevel <= level)
            .Select(definition => definition.Key);

    /// <summary>The keys a built-in role holds on a fresh install (every key for the site Admin).</summary>
    public static IEnumerable<string> DefaultKeysOf(string roleName) =>
        string.Equals(roleName, RoleNames.Admin, StringComparison.Ordinal)
            ? All.Select(definition => definition.Key)
            : All.Where(definition => definition.DefaultRoles.Contains(roleName, StringComparer.Ordinal))
                .Select(definition => definition.Key);

    private static PermissionDefinition[] Levels(string area, ResourceType type, string view, string edit, string manage) =>
    [
        new(view, area, type, Permission.Read, null, Readers),
        new(edit, area, type, Permission.Write, null, Writers),
        new(manage, area, type, Permission.Admin, null, Admins),
    ];

    private static PermissionDefinition Action(
        string key, string area, ResourceType type, Permission legacyLevel, IReadOnlyList<string> defaultRoles) =>
        new(key, area, type, null, legacyLevel, defaultRoles);
}
