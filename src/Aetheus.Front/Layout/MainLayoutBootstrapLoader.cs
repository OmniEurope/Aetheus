// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Layout;

internal static class MainLayoutBootstrapLoader
{
    public static async Task LoadPermissionsAsync(
        ApiClient api,
        PermissionService permissions,
        AuthStateProvider auth,
        ILogger logger)
    {
        try
        {
            var summary = await api.Auth.GetMyPermissionsAsync();
            if (summary is not null)
                permissions.SetPermissions(summary.EffectivePermissions, auth.IsAdmin);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "[MainLayout] Permissions load failed");
        }
    }

    public static async Task LoadOrganizationsAsync(
        ActiveOrganizationService organizations,
        ILogger logger)
    {
        try
        {
            await organizations.RefreshAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "[MainLayout] Organizations load failed");
        }
    }

    /// <summary>
    /// Loads what the routed page needs (permissions, organizations), then starts the task tracker's hub
    /// without waiting for it. The hub reports its state itself (header icon, connection-lost overlay), so
    /// a realtime server slow to answer no longer holds the splash: on a loaded machine the page waited for
    /// the hub's 10 s deadline, then for a liveness probe (PLAN-010 lot 4, sub-lot C, 2026-10-17).
    /// </summary>
    public static async Task StartAuthenticatedSessionAsync(
        ApiClient api,
        PermissionService permissions,
        AuthStateProvider auth,
        ActiveOrganizationService organizations,
        TaskTrackerService taskTracker,
        ILogger logger)
    {
        var bootstrap = new List<Task>(2);
        if (!permissions.IsLoaded) bootstrap.Add(LoadPermissionsAsync(api, permissions, auth, logger));
        if (!organizations.IsLoaded) bootstrap.Add(LoadOrganizationsAsync(organizations, logger));
        if (bootstrap.Count > 0) await Task.WhenAll(bootstrap);

        _ = StartRealtimeAsync(taskTracker, logger);
    }

    private static async Task StartRealtimeAsync(TaskTrackerService taskTracker, ILogger logger)
    {
        try { await taskTracker.StartAsync(); }
        catch (Exception exception) { logger.LogWarning(exception, "[MainLayout] TaskTracker start after login failed"); }
    }
}
