// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>A library or a vault as the usage index sees it: <see cref="OwnerProjectId"/> is the
/// project that owns it directly or through its environment or server, null when it has no owner
/// (readable by every project, as the name resolution of a run treats it).</summary>
public sealed record UsageResourceRow(int Id, string Name, int? OwnerProjectId);

/// <summary>A pipeline's stored definition with the project it runs for.</summary>
public sealed record UsagePipelineRow(int Id, string Name, int? ProjectId, string? ProjectName, int? OrganizationId, string Yaml);

/// <summary>A monitored application that keeps its web analytics keys in the vault named
/// <see cref="VaultName"/> of its project.</summary>
public sealed record UsageAnalyticsAppRow(int Id, string Name, int ProjectId, string VaultName);

/// <summary>The reads behind "used by" on the libraries and vaults pages (recette R-65, R-66).</summary>
public interface IPipelineResourceUsageRepository
{
    Task<List<UsageResourceRow>> GetLibrariesAsync(List<int>? accessibleIds, CancellationToken ct = default);

    Task<List<UsageResourceRow>> GetVaultsAsync(List<int>? accessibleIds, CancellationToken ct = default);

    Task<List<UsagePipelineRow>> GetPipelinesAsync(CancellationToken ct = default);

    Task<List<UsageAnalyticsAppRow>> GetAnalyticsAppsAsync(CancellationToken ct = default);
}
