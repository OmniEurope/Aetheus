// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Central, fail-closed authorization policy for client-side routes. The backend remains the
/// security boundary; this policy prevents an unauthorized page from being instantiated and
/// issuing avoidable requests when a user enters a URL directly.
/// </summary>
public static class RouteAccessPolicy
{
    public static bool CanAccess(
        string relativeUri,
        bool isAuthenticated,
        bool isAdmin,
        PermissionService permissions)
    {
        var path = relativeUri.Split('?', '#')[0].Trim('/');
        // Login is a public transition route. Keep it renderable after LoginAsync has published the
        // authenticated state and while the login component is still preloading permissions before
        // navigating home. Denying it during that short window flashes the global "Access denied"
        // state even though authentication succeeded. An already-authenticated direct visit is still
        // redirected home by Login.OnInitializedAsync.
        if (path.Equals("login", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!isAuthenticated)
            return false;

        if (isAdmin)
            return true;

        if (IsUnrestrictedAuthenticatedPath(path))
            return true;

        // A failed or unfinished permission bootstrap must never open a protected route.
        if (!permissions.IsLoaded)
            return false;

        if (IsAdminPath(path))
            return false;

        if (Matches(path, "servers", out var serverTail))
            return CanAccessResourcePath(serverTail, ResourceType.Server, permissions, "add-agent");

        if (Matches(path, "projects", out var projectTail))
            return CanAccessProjectPath(projectTail, permissions);

        if (Matches(path, "pipelines", out var pipelineTail))
            return CanAccessPipelinePath(pipelineTail, permissions);

        return CanAccessOtherPath(path, permissions);
    }

    private static bool CanAccessPipelinePath(string tail, PermissionService permissions)
    {
        if (tail.Equals("fleet", StringComparison.OrdinalIgnoreCase))
            return permissions.CanReadAny(ResourceType.PipelineTemplate);
        if (tail.StartsWith("runs/", StringComparison.OrdinalIgnoreCase))
            return permissions.CanReadAny(ResourceType.Pipeline);
        return CanAccessResourcePath(tail, ResourceType.Pipeline, permissions, "new");
    }

    private static bool CanAccessOtherPath(string path, PermissionService permissions)
    {
        if (path.Equals("templates", StringComparison.OrdinalIgnoreCase))
            return permissions.CanReadAny(ResourceType.PipelineTemplate);

        if (Matches(path, "releases", out var releaseTail))
            return CanAccessResourcePath(releaseTail, ResourceType.Release, permissions);

        if (Matches(path, "variable-libraries", out var libraryTail))
            return CanAccessResourcePath(libraryTail, ResourceType.VariableLibrary, permissions, "new");

        if (Matches(path, "vaults", out var vaultTail))
            return CanAccessResourcePath(vaultTail, ResourceType.Vault, permissions, "new");

        if (Matches(path, "environments", out var environmentTail))
            return CanAccessResourcePath(environmentTail, ResourceType.Environment, permissions, "new");

        if (Matches(path, "service-connections", out var connectionTail))
            return CanAccessResourcePath(connectionTail, ResourceType.ServiceConnection, permissions, "new");

        return CanAccessReadOnlyPath(path, permissions);
    }

    /// <summary>Routes that only read: operations (tasks, logs, alerts, Supervision) and project
    /// views. Split from <see cref="CanAccessOtherPath"/> to keep each under the grade A complexity.</summary>
    private static bool CanAccessReadOnlyPath(string path, PermissionService permissions)
    {
        if (path.Equals("tasks", StringComparison.OrdinalIgnoreCase)
            || path.Equals("logs", StringComparison.OrdinalIgnoreCase)
            || path.Equals("alerts", StringComparison.OrdinalIgnoreCase))
            return permissions.CanReadAny(ResourceType.Server);

        // R-52: one alert that fired. The API checks Server Read on the alert's own server; which server
        // that is is unknown before the load, so the route asks for Server Read somewhere and the API decides.
        if (Matches(path, "alerts", out var alertTail) && long.TryParse(alertTail, out _))
            return permissions.CanReadAny(ResourceType.Server);

        // FRONT-022: a task's detail and live log (offered by every task list). The API checks Server
        // Read on the task's own server (GET api/tasks/{id}, api/logs/task/{id}); which server that is
        // is unknown before the load, so the route asks for Server Read somewhere and the API decides.
        if (Matches(path, "tasks", out var taskTail) && int.TryParse(taskTail, out _))
            return permissions.CanReadAny(ResourceType.Server);

        // FRONT-022: Supervision, offered by the menu to Project readers. Its summary
        // (GET api/appmonitoring/summary) is filtered to the projects the caller can read.
        if (path.Equals("monitoring", StringComparison.OrdinalIgnoreCase))
            return permissions.CanReadAny(ResourceType.Project);

        if (path.Equals("analysis", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("analysis/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("backups", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("artifacts/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("git", StringComparison.OrdinalIgnoreCase))
            return permissions.CanReadAny(ResourceType.Project);

        // Unknown application routes are denied until explicitly classified here.
        return false;
    }

    private static bool IsUnrestrictedAuthenticatedPath(string path) =>
        string.IsNullOrEmpty(path)
        || path.Equals("settings", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("settings/", StringComparison.OrdinalIgnoreCase)
        // The caller's own notifications; the API scopes every row to the caller.
        || path.Equals("notifications", StringComparison.OrdinalIgnoreCase)
        || path.Equals("help", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("help/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("account/change-password", StringComparison.OrdinalIgnoreCase)
        || path.Equals("not-found", StringComparison.OrdinalIgnoreCase);

    private static bool IsAdminPath(string path) =>
        path.Equals("admin", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("admin/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("users", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("users/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("audit", StringComparison.OrdinalIgnoreCase)
        || path.Equals("plugins", StringComparison.OrdinalIgnoreCase)
        || path.Equals("dashboards", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("dashboards/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("api-reference", StringComparison.OrdinalIgnoreCase);

    private static bool CanAccessResourcePath(
        string tail,
        ResourceType resourceType,
        PermissionService permissions,
        string? createSegment = null)
    {
        if (string.IsNullOrEmpty(tail))
            return permissions.CanReadAny(resourceType);

        if (createSegment is not null && tail.Equals(createSegment, StringComparison.OrdinalIgnoreCase))
            return permissions.CanWrite(resourceType);

        var idSegment = tail.Split('/')[0];
        return int.TryParse(idSegment, out var resourceId)
            ? permissions.CanRead(resourceType, resourceId)
            : permissions.CanReadAny(resourceType);
    }

    private static bool CanAccessProjectPath(string tail, PermissionService permissions)
    {
        if (string.IsNullOrEmpty(tail))
            return permissions.CanReadAny(ResourceType.Project);

        if (tail.Equals("new", StringComparison.OrdinalIgnoreCase))
            return permissions.CanWrite(ResourceType.Project);

        var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!int.TryParse(segments[0], out var projectId))
            return false;

        if (segments.Length == 1)
            return permissions.CanRead(ResourceType.Project, projectId);
        var canReadProject = permissions.CanRead(ResourceType.Project, projectId);
        return CanAccessProjectSection(segments[1], projectId, canReadProject, permissions);
    }

    private static bool CanAccessProjectSection(
        string section,
        int projectId,
        bool canReadProject,
        PermissionService permissions) => section.ToLowerInvariant() switch
        {
            "pipelines" => canReadProject && permissions.CanReadAny(ResourceType.Pipeline),
            "servers" => canReadProject && permissions.CanReadAny(ResourceType.Server),
            "releases" => canReadProject && permissions.CanReadAny(ResourceType.Release),
            "libraries" => canReadProject && permissions.CanReadAny(ResourceType.VariableLibrary),
            "vaults" => canReadProject && permissions.CanReadAny(ResourceType.Vault),
            "environments" => canReadProject && permissions.CanReadAny(ResourceType.Environment),
            "edit" => permissions.CanWrite(ResourceType.Project, projectId),
            _ => permissions.CanRead(ResourceType.Project, projectId)
        };

    private static bool Matches(string path, string prefix, out string tail)
    {
        if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            tail = string.Empty;
            return true;
        }

        var prefixWithSlash = $"{prefix}/";
        if (path.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase))
        {
            tail = path[prefixWithSlash.Length..];
            return true;
        }

        tail = string.Empty;
        return false;
    }
}
