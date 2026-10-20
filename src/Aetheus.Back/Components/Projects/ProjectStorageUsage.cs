// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary> The usage of each project, its rows in each detail table times the bytes per row of
/// that table. Pure, so the arithmetic is tested without a database. </summary>
internal static class ProjectStorageUsage
{
    public static List<ProjectStorageUsageDto> Build(
        IReadOnlyList<ProjectStorageBudget> budgets,
        IReadOnlyList<ProjectTableRows> rows,
        IReadOnlyDictionary<string, double> bytesPerRow)
    {
        var byProject = rows.ToLookup(row => row.ProjectId);
        return [.. budgets
            .Select(budget =>
            {
                var tables = byProject[budget.ProjectId]
                    .Select(row => new ProjectStorageTableDto(
                        row.Table, row.Rows, (long)Math.Round(row.Rows * BytesOf(bytesPerRow, row.Table))))
                    .OrderByDescending(table => table.Bytes)
                    .ToList();
                var used = tables.Sum(table => table.Bytes);
                return new ProjectStorageUsageDto
                {
                    ProjectId = budget.ProjectId,
                    ProjectName = budget.Name,
                    BudgetBytes = budget.BudgetBytes,
                    UsedBytes = used,
                    UsedPercent = Percent(used, budget.BudgetBytes),
                    Tables = tables
                };
            })
            .OrderByDescending(usage => usage.UsedBytes)
            .ThenBy(usage => usage.ProjectName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The alert level a usage reaches: 100 at the budget, 80 from 80 %, 0 below.</summary>
    public static int AlertLevel(int usedPercent) =>
        usedPercent >= 100 ? 100 : usedPercent >= ProjectStorageDefaults.AlertPercent ? ProjectStorageDefaults.AlertPercent : 0;

    public static double BytesOf(IReadOnlyDictionary<string, double> bytesPerRow, string table) =>
        bytesPerRow.TryGetValue(table, out var bytes) ? bytes : 0;

    private static int Percent(long used, long budget) =>
        budget <= 0 ? 0 : (int)Math.Min(int.MaxValue, used * 100 / budget);
}
