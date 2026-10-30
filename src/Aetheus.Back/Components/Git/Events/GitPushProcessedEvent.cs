// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Git.Events;

/// <summary>Announces a committed Git push so Pipelines can handle orchestration without an upward
/// Git dependency (ADR-039). Observer dispatch keeps downstream trigger failures from invalidating
/// an already-persisted push.</summary> <param name="ProjectId">Project the repository belongs
/// to.</param> <param name="GitRepoId">Internal repository id.</param> <param
/// name="Slug">Repository slug, for diagnostics.</param> <param name="RepositoryName">Repository
/// display name, for diagnostics.</param> <param name="DefaultBranch">Default branch after the
/// push.</param> <param name="DiskPath"> Resolved, validated on-disk path of the bare repository.
/// Carried rather than re-derived: the path is subject to a traversal check that belongs to the Git
/// module, and a subscriber must not be able to reconstruct it from a slug. </param> <param
/// name="RefUpdates">Reference updates this push carried.</param>
public sealed record GitPushProcessedEvent(
    int ProjectId,
    int GitRepoId,
    string Slug,
    string RepositoryName,
    string? DefaultBranch,
    string DiskPath,
    IReadOnlyList<GitRefUpdate> RefUpdates,
    string? PusherUsername = null) : IGitRefUpdatesEvent;
