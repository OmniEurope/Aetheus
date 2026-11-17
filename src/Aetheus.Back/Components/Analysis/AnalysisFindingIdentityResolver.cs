// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Analysis;

/// <summary>
/// Finds the stored finding a parsed one is, when its identity moved to the vulnerability and the package
/// (<see cref="VulnerabilityIdentity"/>, recette R-2): under its current fingerprint, under the fingerprint it had
/// before (adopted, so its decisions and history stay with it), or under an alias identifier of the same
/// vulnerability (used as it is, so the GHSA a scanner stored and the CVE another one reports are one finding).
/// </summary>
internal static class AnalysisFindingIdentityResolver
{
    /// <summary>Every fingerprint the stored findings of the parsed ones can be looked up by.</summary>
    public static List<string> CandidateFingerprints(IEnumerable<ParsedAnalysisFinding> parsed) =>
        parsed
            .SelectMany(item => item.AlternateFingerprints.Prepend(item.LegacyFingerprint).Prepend(item.Fingerprint))
            .Where(fingerprint => fingerprint.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>The stored finding of the parsed one, or null when it was never seen. A finding found under its
    /// former fingerprint is re-keyed to the current one, in the lookup too, so the next scanner finds it.</summary>
    public static AnalysisFinding? Resolve(Dictionary<string, AnalysisFinding> stored, ParsedAnalysisFinding item)
    {
        if (stored.TryGetValue(item.Fingerprint, out var current)) return current;
        if (item.LegacyFingerprint.Length > 0 && stored.Remove(item.LegacyFingerprint, out var legacy))
        {
            legacy.Fingerprint = item.Fingerprint;
            stored[item.Fingerprint] = legacy;
            return legacy;
        }
        foreach (var alternate in item.AlternateFingerprints)
            if (stored.TryGetValue(alternate, out var alias)) return alias;
        return null;
    }

    /// <summary>Whether a baseline (the fingerprints of the previous run) already held the finding, under whichever
    /// fingerprint it was stored then.</summary>
    public static bool WasInBaseline(ISet<string> baseline, ParsedAnalysisFinding item, AnalysisFinding finding) =>
        baseline.Contains(item.Fingerprint)
        || baseline.Contains(finding.Fingerprint)
        || (item.LegacyFingerprint.Length > 0 && baseline.Contains(item.LegacyFingerprint))
        || item.AlternateFingerprints.Any(baseline.Contains);
}
