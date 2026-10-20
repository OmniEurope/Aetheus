// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.Releases;

/// <summary>
/// Recette R-13: a release whose recorded status is wrong (the release production still serves left
/// Failed by an older rollback rule) is put back by hand. Only the statuses a deployment history can
/// end in are offered; the backend refuses the others and the front lists the same ones.
/// </summary>
public static class ReleaseStatusRestore
{
    public const int MaxReasonLength = 500;

    public static IReadOnlyList<ReleaseStatus> Targets { get; } =
        [ReleaseStatus.Deployed, ReleaseStatus.Superseded, ReleaseStatus.Published, ReleaseStatus.Failed];

    /// <summary>An archived release lost its artifacts and runs: only a new publication brings it back.</summary>
    public static bool CanRestoreFrom(ReleaseStatus current) => current != ReleaseStatus.Archived;

    public static bool CanRestore(ReleaseStatus current, ReleaseStatus target) =>
        CanRestoreFrom(current) && current != target && Targets.Contains(target);
}

/// <summary>Recette R-13: the status a release is put back to, and why (kept in the audit log).</summary>
public sealed record RestoreReleaseStatusRequest
{
    [Required]
    public ReleaseStatus Status { get; init; }

    [Required]
    [StringLength(ReleaseStatusRestore.MaxReasonLength, MinimumLength = 1)]
    public string Reason { get; init; } = string.Empty;
}
