// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Projects;

/// <summary>
/// Recette R2-122: the identifiers of everything a project owns, read once inside the deletion
/// transaction. Owned means owned by the project itself, by one of its environments or by one of its
/// project servers (the three owners a pipeline, a vault or a library can have). A server record is
/// never in the scope: a project server only links to it, and removing the link detaches the server.
/// </summary>
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
