// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The release a <c>type: advance-branch</c> step moves a branch onto, read by the
/// orchestrator over the shared entity (the Releases module sits above it). <paramref
/// name="CommitHash"/> is the commit the release records, i.e. the one its candidate
/// built.</summary>
public sealed record BranchAdvanceRelease(int Id, string Version, ReleaseStatus Status, string? CommitHash);
