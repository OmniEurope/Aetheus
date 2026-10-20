// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>The storage budget of each project, its usage and its enforcement.</summary>
public interface IProjectStorageBudgetService
{
    /// <summary>The usage of the projects the caller can read (all of them when null).</summary>
    Task<ProjectStorageReportDto> GetReportAsync(IReadOnlyCollection<int>? accessibleProjectIds, CancellationToken ct = default);

    /// <summary>Alerts at 80 % and purges the oldest detail of every project over its budget.</summary>
    Task<ProjectStorageSweep> EnforceAsync(CancellationToken ct = default);
}
