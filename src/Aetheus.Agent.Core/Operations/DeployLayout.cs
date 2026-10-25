// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Pure, side-effect-light filesystem helpers for <see cref="DeployOperationExecutor"/>. Extracted
/// as <c>internal static</c> so the security- and correctness-critical bits (zip-slip guard, run
/// launcher discovery, release pruning, free-space gate) are unit-testable off-Linux without a shell
/// or a real systemd. The executor stays the thin I/O orchestrator.
/// </summary>
internal static class DeployLayout
{
    internal const int MaxArchiveEntries = 20_000;
    internal const long MaxExpandedArchiveBytes = 8L * 1024 * 1024 * 1024;
    /// <summary>Default recent release dirs kept per app (plus whatever <c>current</c>/<c>previous</c>
    /// point at), the rest are pruned after a successful deploy to bound disk on the single prod VPS.
    /// DEPV: overridable via <c>AetheusAgentOptions.DeployReleasesToKeep</c>; this is the fallback.</summary>
    internal const int ReleasesToKeep = 3;

    /// <summary>Default free-space floor: refuse to extract unless at least this much headroom is free, on
    /// top of an estimate of the unpacked payload, so a self-contained app never fills the disk under the
    /// running app + DB. DEPV: overridable via <c>AetheusAgentOptions.DeployMinFreeSpaceMiB</c>.</summary>
    internal const long MinFreeSpaceBytes = 512L * 1024 * 1024; // 512 MiB floor

    /// <summary>Default heuristic unpacked-to-compressed ratio for the free-space estimate (publish trees
    /// compress ~2–3×; 4× leaves margin). DEPV: overridable via <c>AetheusAgentOptions.DeployUnpackRatioEstimate</c>.</summary>
    internal const int UnpackRatioEstimate = 4;

    // Exactly one trailing separator (GetFullPath may or may not keep one) - for prefix containment checks.
    private static string WithTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    // A relative path that stays inside its base: not rooted, no ".." segment, not empty.
    internal static bool IsSafeRelativePath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && !Path.IsPathRooted(path)
           && !path.Split('/', '\\').Contains("..");

    /// <summary>
    /// Zip-slip guard: resolve a zip entry's on-disk path and assert it stays under <paramref name="destDir"/>.
    /// Throws <see cref="InvalidDataException"/> on escape. Returns null for a directory entry.
    /// </summary>
    internal static string? ResolveSafeEntryPath(string destDir, string entryFullName)
    {
        var root = WithTrailingSeparator(Path.GetFullPath(destDir));
        var target = Path.GetFullPath(Path.Combine(destDir, entryFullName));
        if (!target.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException($"Zip entry escapes target directory: {entryFullName}");
        return entryFullName.EndsWith('/') ? null : target;
    }

    internal static long ValidateArchive(
        ZipArchive archive,
        string destDir,
        int maxEntries = MaxArchiveEntries,
        long maxExpandedBytes = MaxExpandedArchiveBytes)
    {
        long expandedBytes = 0;
        var entries = 0;
        foreach (var entry in archive.Entries)
        {
            ResolveSafeEntryPath(destDir, entry.FullName);
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException($"Symbolic-link archive entry is forbidden: {entry.FullName}");
            if (++entries > maxEntries)
                throw new InvalidDataException($"Deploy archive exceeds the {maxEntries} entry safety limit.");
            if (entry.Length < 0 || entry.Length > maxExpandedBytes - expandedBytes)
                throw new InvalidDataException(
                    $"Deploy archive expands beyond the {maxExpandedBytes / (1024 * 1024)} Mo safety limit.");
            expandedBytes += entry.Length;
        }
        if (entries == 0) throw new InvalidDataException("Deploy archive is empty.");
        return expandedBytes;
    }

    // Extract every entry through the zip-slip/link/quota guards.
    internal static void ExtractSafely(ZipArchive archive, string destDir)
    {
        _ = ValidateArchive(archive, destDir);
        foreach (var entry in archive.Entries)
        {
            var target = ResolveSafeEntryPath(destDir, entry.FullName);
            if (target is null)
            {
                // nosemgrep: aetheus.csharp.archive-entry-path-traversal - ResolveSafeEntryPath above already rejected any entry leaving destDir.
                Directory.CreateDirectory(Path.GetFullPath(Path.Combine(destDir, entry.FullName)));
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// The directory that should become <c>current</c>: the one holding the <c>run</c> launcher. The
    /// artifact collector preserves the publish sub-path (e.g. <c>publish/toto/run</c>), so <c>run</c>
    /// is frequently NOT at the release root - find it. Returns the release root when <c>run</c> is
    /// there, the unique sub-directory that holds it otherwise, or null when absent/ambiguous (the
    /// caller then fails honestly rather than starting a unit with no entrypoint).
    /// </summary>
    internal static string? ResolveAppRoot(string releaseDir)
    {
        if (File.Exists(Path.Combine(releaseDir, "run"))) return releaseDir;
        var runs = Directory.EnumerateFiles(releaseDir, "run", SearchOption.AllDirectories).Take(2).ToList();
        return runs.Count == 1 ? Path.GetDirectoryName(runs[0]) : null;
    }

    /// <summary>
    /// Map a <c>current</c>/<c>previous</c> symlink target back to its immediate child-of-<paramref name="releasesRoot"/>
    /// directory (the app root may be a sub-dir of the release dir). Null when the target is outside.
    /// </summary>
    internal static string? TopLevelReleaseDir(string? target, string releasesRoot)
    {
        if (string.IsNullOrEmpty(target)) return null;
        var rootFull = WithTrailingSeparator(Path.GetFullPath(releasesRoot));
        var cur = Path.GetFullPath(target);
        while (true)
        {
            var parent = Path.GetDirectoryName(cur);
            if (parent is null) return null;
            if (string.Equals(WithTrailingSeparator(parent), rootFull, StringComparison.Ordinal))
                return cur;
            cur = parent;
        }
    }

    /// <summary>
    /// Choose release dirs to delete: keep the <paramref name="keep"/> most-recently-written, and never
    /// touch a protected dir (what <c>current</c>/<c>previous</c> resolve to). Pure - the caller does
    /// the deletion. <paramref name="releaseDirsByRecencyDesc"/> is newest-first.
    /// </summary>
    internal static List<string> SelectPrunableReleaseDirs(
        IReadOnlyList<string> releaseDirsByRecencyDesc, int keep, ISet<string> protectedDirs)
    {
        var prune = new List<string>();
        var kept = 0;
        foreach (var dir in releaseDirsByRecencyDesc)
        {
            if (protectedDirs.Contains(dir)) continue; // protected dirs never count against the budget
            if (kept < keep) { kept++; continue; }
            prune.Add(dir);
        }
        return prune;
    }

    /// <summary>Free-space gate: is there room for the unpacked payload plus the floor headroom?
    /// DEPV: the floor and ratio are caller-supplied (sourced from <c>AetheusAgentOptions</c>).</summary>
    internal static bool HasEnoughFreeSpace(long availableBytes, long compressedBytes, long minFreeSpaceBytes, int unpackRatio)
        => availableBytes >= minFreeSpaceBytes + Math.Max(0, compressedBytes) * unpackRatio;

    internal static bool HasEnoughFreeSpaceForExpandedPayload(
        long availableBytes, long expandedBytes, long minFreeSpaceBytes) =>
        availableBytes >= minFreeSpaceBytes + Math.Max(0, expandedBytes);

    /// <summary>Free-space gate using the built-in defaults (back-compat overload for tests/callers
    /// that don't override the tuning).</summary>
    internal static bool HasEnoughFreeSpace(long availableBytes, long compressedBytes)
        => HasEnoughFreeSpace(availableBytes, compressedBytes, MinFreeSpaceBytes, UnpackRatioEstimate);

    /// <summary>Health-gate window floor (s): below this the dwell logic (RequiredStableSamples ×
    /// PollSeconds, which must exceed the unit's RestartSec) cannot reliably observe a full restart cycle.</summary>
    internal const int HealthWindowFloorSeconds = 20;

    /// <summary>Upper bound (s) for the timeout-DERIVED window only. An explicit <c>health_timeout</c> is
    /// NOT capped here (its whole purpose is to allow a longer wait for a slow cold start) - only by the
    /// overall step timeout, which the window can never outlast.</summary>
    internal const int DerivedHealthWindowCapSeconds = 120;

    /// <summary>
    /// Resolve the post-deploy health-gate stabilization window (seconds). When
    /// <paramref name="healthTimeoutSeconds"/> is positive the operator's explicit value wins - clamped
    /// to <see cref="HealthWindowFloorSeconds"/> and the overall step timeout (the window can never
    /// outlast the step), decoupling a slow .NET cold start (JIT + EF migrations) from the timeout/3
    /// heuristic so a legitimately slow boot is not mistaken for a crash loop. Otherwise the window is
    /// derived from the step timeout (timeout/3), clamped to [floor, <see cref="DerivedHealthWindowCapSeconds"/>].
    /// </summary>
    internal static int ResolveHealthWindowSeconds(int timeoutSeconds, int healthTimeoutSeconds)
    {
        if (healthTimeoutSeconds > 0)
        {
            var cap = Math.Max(HealthWindowFloorSeconds, timeoutSeconds);
            return Math.Clamp(healthTimeoutSeconds, HealthWindowFloorSeconds, cap);
        }
        return Math.Clamp(timeoutSeconds / 3, HealthWindowFloorSeconds, DerivedHealthWindowCapSeconds);
    }

    /// <summary>Parsed <c>systemctl show</c> health properties.</summary>
    internal readonly record struct UnitState(string ActiveState, string SubState, int NRestarts)
    {
        /// <summary>The only state that means the app actually came up and stayed up.</summary>
        internal bool IsActiveRunning =>
            string.Equals(ActiveState, "active", StringComparison.Ordinal)
            && string.Equals(SubState, "running", StringComparison.Ordinal);
    }

    internal enum HealthDecision { Pass, CrashLooped, Continue }

    /// <summary>
    /// Pure per-sample health-gate decision, extracted so the crash-loop logic is unit-testable
    /// without a real process / <c>Task.Delay</c>. Returns <see cref="HealthDecision.CrashLooped"/>
    /// the moment NRestarts climbs above the first-seen baseline, <see cref="HealthDecision.Pass"/>
    /// once a CONTINUOUS running dwell reaches <paramref name="requiredStableSamples"/> (so a
    /// warmup-then-crash loop can't false-pass on two early running reads), else Continue.
    /// </summary>
    internal static HealthDecision EvaluateHealthSample(
        UnitState state, ref int? baselineRestarts, ref int stableHits, int requiredStableSamples)
    {
        baselineRestarts ??= state.NRestarts;
        if (state.NRestarts > baselineRestarts)
            return HealthDecision.CrashLooped;

        stableHits = state.IsActiveRunning ? stableHits + 1 : 0;
        return stableHits >= requiredStableSamples ? HealthDecision.Pass : HealthDecision.Continue;
    }

    /// <summary>
    /// Parse <c>systemctl show &lt;unit&gt; --property=ActiveState --property=SubState --property=NRestarts</c>
    /// (key=value lines, any order). Defeats the <c>Restart=always</c> false-green: a crash-looping
    /// binary flaps <c>is-active</c> but its <c>NRestarts</c> climbs and <c>SubState</c> is rarely
    /// <c>running</c> at the sample point - the executor watches both, not a single <c>is-active</c>.
    /// </summary>
    internal static UnitState ParseUnitState(string showOutput)
    {
        string active = "", sub = "";
        var restarts = 0;
        foreach (var raw in (showOutput ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var val = line[(eq + 1)..];
            switch (key)
            {
                case "ActiveState": active = val; break;
                case "SubState": sub = val; break;
                case "NRestarts": _ = int.TryParse(val, out restarts); break;
            }
        }
        return new UnitState(active, sub, restarts);
    }
}
