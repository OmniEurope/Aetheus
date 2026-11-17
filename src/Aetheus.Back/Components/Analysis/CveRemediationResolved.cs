// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Back.Components.Inventory;
using Aetheus.Back.Components.VulnerabilityWatch.VersionComparison;

namespace Aetheus.Back.Components.Analysis;

/// <summary>What the lock files really hold, against the target: the version, or why it cannot be trusted.</summary>
internal sealed record CveRemediationResolution(string? Version, string? Failure);

/// <summary>
/// PLAN-004, lot 4, point 12: a bump raises a floor, so npm and Composer may resolve above the target, and a nested copy may stay below
/// it. What counts is what the lock file holds after the package manager ran. The answer is the lowest version it holds, the one
/// that decides whether the advisory still applies. A pure function of the files.
/// </summary>
internal static class CveRemediationResolved
{
    public static CveRemediationResolution Read(string ecosystem, string package, string target, IReadOnlyList<(string Path, string Content)> locks)
    {
        // A runtime is stated by the manifest; composer.lock only follows its content hash.
        if (string.Equals(ecosystem, "runtime", StringComparison.Ordinal)) return new CveRemediationResolution(null, null);

        string? lowest = null;
        foreach (var (path, content) in locks)
        {
            var name = path[(path.LastIndexOf('/') + 1)..];
            // A byte order mark says nothing about the versions, and the JSON reader refuses it.
            var versions = LockFileVersions.Read(name, Encoding.UTF8.GetBytes(content.TrimStart('﻿')), package);
            if (versions is null) return new CveRemediationResolution(null, $"'{path}' does not read as a lock file.");
            if (versions.Count == 0) return new CveRemediationResolution(null, $"'{path}' does not hold {package}.");
            foreach (var version in versions)
            {
                var order = VersionComparators.Compare(ecosystem, version, target);
                if (order == VersionOrder.Unknown) return new CveRemediationResolution(null, $"{version} and {target} cannot be ordered.");
                if (order == VersionOrder.Less) return new CveRemediationResolution(version, $"'{path}' still holds {package} {version}, below {target}.");
                if (lowest is null || VersionComparators.Compare(ecosystem, version, lowest) == VersionOrder.Less) lowest = version;
            }
        }

        return new CveRemediationResolution(lowest, null);
    }
}
