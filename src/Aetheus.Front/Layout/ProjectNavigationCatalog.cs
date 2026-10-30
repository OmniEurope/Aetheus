// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Layout;

internal enum ProjectNavigationAccess
{
    None,
    PipelineRead,
    ServerRead,
    ReleaseRead,
    VariableLibraryRead,
    VaultRead,
    EnvironmentRead,
    ProjectWrite
}

internal sealed record ProjectNavigationItem(
    string LabelKey,
    string Icon,
    string Slug,
    ProjectNavigationAccess Access = ProjectNavigationAccess.None)
{
    public string Path(int projectId) => Slug == "git"
        ? $"git-repositories?projectId={projectId}"
        : $"projects/{projectId}/{Slug}";

    public string Href(int projectId) => $"/{Path(projectId)}";
}

internal static class ProjectNavigationCatalog
{
    private static readonly ProjectNavigationItem[] Items =
    [
        new("Overview", "dashboard", "overview"),
        new("Pipelines", "account_tree", "pipelines", ProjectNavigationAccess.PipelineRead),
        new("AnalysisPortfolio", "shield", "quality"),
        new("Releases", "new_releases", "releases", ProjectNavigationAccess.ReleaseRead),
        new("Artifacts", "inventory_2", "artifacts"),
        new("Libraries", "library_books", "libraries", ProjectNavigationAccess.VariableLibraryRead),
        new("Vaults", "lock", "vaults", ProjectNavigationAccess.VaultRead),
        new("Servers", "dns", "servers", ProjectNavigationAccess.ServerRead),
        new("Environments", "layers", "environments", ProjectNavigationAccess.EnvironmentRead),
        new("Monitoring", "monitor_heart", "monitoring"),
        new("Tasks", "task_alt", "tasks"),
        new("Logs", "article", "logs"),
        new("Git", "commit", "git"),
        // Recette R3-70: the project's settings page, titled Settings rather than Edit.
        new("Settings", "settings", "edit", ProjectNavigationAccess.ProjectWrite)
    ];

    /// <summary>The icon of a section, shown before its title; null for an unknown slug.</summary>
    public static string? IconFor(string slug) =>
        Items.FirstOrDefault(item => string.Equals(item.Slug, slug, StringComparison.OrdinalIgnoreCase))?.Icon;

    public static IEnumerable<ProjectNavigationItem> VisibleItems(
        PermissionService permissions,
        int projectId) => Items.Where(item => IsVisible(item, permissions, projectId));

    private static bool IsVisible(
        ProjectNavigationItem item,
        PermissionService permissions,
        int projectId) => item.Access switch
        {
            ProjectNavigationAccess.None => true,
            ProjectNavigationAccess.PipelineRead => permissions.CanReadAny(ResourceType.Pipeline),
            ProjectNavigationAccess.ServerRead => permissions.CanReadAny(ResourceType.Server),
            ProjectNavigationAccess.ReleaseRead => permissions.CanReadAny(ResourceType.Release),
            ProjectNavigationAccess.VariableLibraryRead => permissions.CanReadAny(ResourceType.VariableLibrary),
            ProjectNavigationAccess.VaultRead => permissions.CanReadAny(ResourceType.Vault),
            ProjectNavigationAccess.EnvironmentRead => permissions.CanReadAny(ResourceType.Environment),
            ProjectNavigationAccess.ProjectWrite => permissions.CanWrite(ResourceType.Project, projectId),
            _ => false
        };
}
