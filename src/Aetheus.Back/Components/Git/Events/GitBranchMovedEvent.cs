// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Git.Events;

/// <summary>Announces a branch the server moved itself (the post-deployment branch advance, a merged
/// pull request), without <c>receive-pack</c>. It exists beside <see cref="GitPushProcessedEvent"/>
/// on purpose: a subscriber that keeps state derived from the branch content, such as the pipeline
/// definitions read from <c>.pipeline/</c>, must follow the branch however it moved, but moving a
/// branch onto a commit that was already built and deployed must not behave as a push and
/// re-trigger the pipelines that watch it (ADR-044). Dispatched as an observer: a failing subscriber
/// never undoes the ref update that already happened.</summary>
public sealed record GitBranchMovedEvent(
    int ProjectId,
    int GitRepoId,
    string Slug,
    string RepositoryName,
    string? DefaultBranch,
    string DiskPath,
    IReadOnlyList<GitRefUpdate> RefUpdates,
    string? PusherUsername = null) : IGitRefUpdatesEvent;
