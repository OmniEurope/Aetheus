// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>Rows per project in the detail tables, the budgets, and the oldest-first
/// purges.</summary>
public interface IProjectStorageRepository
{
    /// <summary>The name of the database Aetheus is connected to, to find its own size measurement.</summary>
    string DatabaseName { get; }

    Task<IReadOnlyList<ProjectStorageBudget>> GetBudgetsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProjectTableRows>> CountRowsAsync(CancellationToken ct = default);

    /// <summary>The applications that exported a database size measurement since <paramref name="sinceUtc"/>.</summary>
    Task<IReadOnlyList<int>> GetAppsMeasuringADatabaseAsync(DateTime sinceUtc, CancellationToken ct = default);

    Task SetAlertLevelAsync(int projectId, int level, CancellationToken ct = default);
    Task<DateTime?> GetOldestSampleAsync(int projectId, CancellationToken ct = default);
    Task<DateTime?> GetOldestLogAsync(int projectId, CancellationToken ct = default);
    Task<int> DeleteOldestSamplesAsync(int projectId, int batchSize, CancellationToken ct = default);
    Task<int> DeleteOldestLogsAsync(int projectId, int batchSize, CancellationToken ct = default);
}

public sealed record ProjectStorageBudget(int ProjectId, string Name, long BudgetBytes, int AlertLevel);

public sealed record ProjectTableRows(int ProjectId, string Table, long Rows);
