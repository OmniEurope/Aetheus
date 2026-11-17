// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Analysis.DependencyBump;

/// <summary>What one bump asks for: the match as the inventory holds it, and the version to move to.</summary>
/// <param name="Ecosystem">As the inventory spells it: <c>NuGet</c>, <c>npm</c> or <c>Packagist</c>.</param>
/// <param name="PackageName">As the inventory spells it.</param>
/// <param name="CurrentVersion">The version the match has, the one the bump must find in the manifest.</param>
/// <param name="TargetVersion">The version to move to. Comes from a registry and an advisory, so it is checked before it is written anywhere.</param>
/// <param name="SourcePath">The file that names the package, as the recommendation gives it (<c>CveWatchRecommendationDto.Files</c>), relative to the repository root.</param>
/// <param name="TargetDigest">Only for a <c>docker</c> image the repository pins by digest (a <paramref name="CurrentVersion"/> of the form
/// <c>tag@sha256:...</c>): the digest of the target tag, as a registry gave it. A plan never derives it from a version, so a digest-pinned
/// image with no digest is refused, and a digest is never written onto a line that did not pin one.</param>
public sealed record DependencyBumpRequest(
    string Ecosystem, string PackageName, string CurrentVersion, string TargetVersion, string SourcePath, string? TargetDigest = null);

/// <summary>How far the bump got. Only <see cref="Complete"/> leaves the repository consistent as it stands.</summary>
public enum DependencyBumpOutcome
{
    /// <summary>Every file that has to change is in <see cref="DependencyBumpResult.Edits"/>, and nothing else has to be refreshed.</summary>
    Complete,

    /// <summary>The manifest edits are right, but a lock file still describes the old version: only its package manager can
    /// refresh it, and <see cref="DependencyBumpResult.LockRefreshes"/> says how. Committing the edits alone would leave
    /// the repository inconsistent.</summary>
    NeedsPackageManager,

    /// <summary>Nothing was edited: <see cref="DependencyBumpResult.Refusal"/> and <see cref="DependencyBumpResult.Reason"/> say why.</summary>
    Refused
}

/// <summary>Why a bump was refused, for the interface to word and to pick the fallback command from.</summary>
public enum DependencyBumpRefusal
{
    None,

    /// <summary>A field of the request is empty, a path leaves the repository, or the target version is not a version.</summary>
    InvalidRequest,

    /// <summary>No bump is written for this ecosystem.</summary>
    UnsupportedEcosystem,

    /// <summary>The package is held by a lock file of a package manager this bump does not drive (pnpm, Yarn).</summary>
    UnsupportedLockFile,

    /// <summary>The target is not above the current version, or the two cannot be ordered: a bump never moves down or sideways.</summary>
    TargetNotAbove,

    /// <summary>The source file, or the manifest next to a lock file, is not among the files given.</summary>
    ManifestNotFound,

    /// <summary>No manifest names the package at the current version: it comes in through another package.</summary>
    NotDeclared,

    /// <summary>The manifest names the package, but not as a version this bump can change without guessing: a property, a range, a tag, an address.</summary>
    VersionNotPinned,

    /// <summary>A manifest does not read as the format it claims to be.</summary>
    UnreadableManifest,

    /// <summary>The edit was written, then did not verify against the original: nothing is returned.</summary>
    EditNotVerified,

    /// <summary>The manifest already asks for the target and no lock file lags behind it: there is nothing to do, and saying
    /// "done" would be a success that changed nothing.</summary>
    NothingToChange
}

/// <summary>One place a version was changed in a file.</summary>
/// <param name="Line">The line of the changed value, counted from 1.</param>
public sealed record DependencyBumpChange(string Description, int Line);

/// <summary>A file to write: its whole new content, and what changed in it.</summary>
public sealed record DependencyBumpFileEdit(string Path, string Content, IReadOnlyList<DependencyBumpChange> Changes);

/// <summary>A lock file that stays behind until a package manager runs, and the command that refreshes it.</summary>
/// <param name="LockFile">The path of the lock file.</param>
/// <param name="WorkingDirectory">The directory to run the command in, relative to the repository root ("" is the root).</param>
/// <param name="Command">The exact command, which changes the lock file and nothing else of the manifest.</param>
public sealed record DependencyBumpLockRefresh(string LockFile, string WorkingDirectory, string Command);

/// <summary>The answer to a bump request: the edits to write, or the reason there are none. Never an empty success.</summary>
public sealed record DependencyBumpResult(
    DependencyBumpOutcome Outcome,
    DependencyBumpRefusal Refusal,
    string? Reason,
    IReadOnlyList<DependencyBumpFileEdit> Edits,
    IReadOnlyList<DependencyBumpLockRefresh> LockRefreshes)
{
    public static DependencyBumpResult Refused(DependencyBumpRefusal refusal, string reason) =>
        new(DependencyBumpOutcome.Refused, refusal, reason, [], []);

    /// <summary>The edits and the lock files left to refresh. With neither there is nothing to do, which is a refusal, not a success.</summary>
    public static DependencyBumpResult Done(
        IReadOnlyList<DependencyBumpFileEdit> edits, IReadOnlyList<DependencyBumpLockRefresh> locks, string? reason = null) =>
        edits.Count == 0 && locks.Count == 0
            ? Refused(DependencyBumpRefusal.NothingToChange, "The manifest already asks for the target version and no lock file lags behind it.")
            : new(locks.Count == 0 ? DependencyBumpOutcome.Complete : DependencyBumpOutcome.NeedsPackageManager,
                DependencyBumpRefusal.None, reason, edits, locks);
}
