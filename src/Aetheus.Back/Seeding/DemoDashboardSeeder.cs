// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Seeding;

internal static class DemoDashboardSeeder
{
    internal static async Task SeedAsync(AppDbContext db, DateTime now)
    {
        // Dashboards are user-owned rather than organization-owned. The mandatory bootstrap creates
        // the admin account before this optional seed runs in local/QA; keep the guard so isolated
        // tests that intentionally seed only an organization remain valid no-ops here.
        var adminUserId = await db.Users.Where(user => user.Username == "admin")
            .Select(user => user.Id).FirstOrDefaultAsync().ConfigureAwait(false);
        if (adminUserId == 0) return;

        var dashboardSeeds = new[]
        {
            new Dashboard { UserId = adminUserId, Name = "Vue exploitation", IsDefault = true, CreatedAt = now, UpdatedAt = now },
            new Dashboard { UserId = adminUserId, Name = "Qualité des livraisons", IsDefault = false, CreatedAt = now, UpdatedAt = now }
        };
        var dashboardNames = dashboardSeeds.Select(dashboard => dashboard.Name).ToArray();
        var existingDashboards = await db.Dashboards
            .Where(dashboard => dashboard.UserId == adminUserId && dashboardNames.Contains(dashboard.Name))
            .ToListAsync().ConfigureAwait(false);
        db.Dashboards.AddRange(dashboardSeeds.Where(candidate =>
            !existingDashboards.Any(existing => existing.Name == candidate.Name)));
        await db.SaveChangesAsync().ConfigureAwait(false);
        var dashboards = dashboardSeeds.Select(candidate =>
            existingDashboards.FirstOrDefault(existing => existing.Name == candidate.Name) ?? candidate).ToArray();

        var widgetSeeds = new[]
        {
            new DashboardWidget { DashboardId = dashboards[0].Id, WidgetType = DashboardWidgetType.ServerCount, Title = "État du parc", Column = 0, Row = 0, Width = 1, Height = 1 },
            new DashboardWidget { DashboardId = dashboards[0].Id, WidgetType = DashboardWidgetType.ServerList, Title = "Serveurs récents", Column = 1, Row = 0, Width = 2, Height = 2 },
            new DashboardWidget { DashboardId = dashboards[0].Id, WidgetType = DashboardWidgetType.TaskSummary, Title = "Tâches en cours", Column = 0, Row = 1, Width = 1, Height = 1 },
            new DashboardWidget { DashboardId = dashboards[1].Id, WidgetType = DashboardWidgetType.PipelineActivity, Title = "Activité pipeline", Column = 0, Row = 0, Width = 2, Height = 2 },
            new DashboardWidget { DashboardId = dashboards[1].Id, WidgetType = DashboardWidgetType.RecentRuns, Title = "Runs récents", Column = 2, Row = 0, Width = 1, Height = 2 },
            new DashboardWidget { DashboardId = dashboards[1].Id, WidgetType = DashboardWidgetType.BuildSuccess, Title = "Taux de succès", Column = 0, Row = 2, Width = 1, Height = 1, ConfigurationJson = "{\"days\":7}" }
        };
        var dashboardIds = dashboards.Select(dashboard => dashboard.Id).ToArray();
        var existingWidgetKeys = await db.DashboardWidgets
            .Where(widget => dashboardIds.Contains(widget.DashboardId))
            .Select(widget => new { widget.DashboardId, widget.Title }).ToListAsync().ConfigureAwait(false);
        db.DashboardWidgets.AddRange(widgetSeeds.Where(candidate => !existingWidgetKeys.Any(existing =>
            existing.DashboardId == candidate.DashboardId && existing.Title == candidate.Title)));
    }
}
