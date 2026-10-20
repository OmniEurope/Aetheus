// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>Captures identifiers owned by the project, its environments and its project servers
/// once within the deletion transaction. Linked server records are excluded: deleting a project
/// server detaches the server.</summary>
public sealed record ProjectDeletionScope(
    int ProjectId,
    string ProjectName,
    int OrganizationId,
    IReadOnlyList<int> EnvironmentIds,
    IReadOnlyList<int> ProjectServerIds,
    IReadOnlyList<int> PipelineIds,
    IReadOnlyList<int> MonitoredAppIds,
    IReadOnlyList<int> VaultIds,
    IReadOnlyList<int> LibraryIds,
    IReadOnlyList<int> ReleaseIds,
    IReadOnlyList<int> ServiceConnectionIds,
    IReadOnlyList<int> PackageFeedIds);
