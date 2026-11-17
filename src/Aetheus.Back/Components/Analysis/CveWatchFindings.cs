// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Back.Components.Analysis;

/// <summary>One finding of the watch: every match of a project that stands for one CVE in one package.</summary>
internal sealed record CveWatchFindingDraft(
    string Fingerprint,
    string RuleId,
    AnalysisCategory Category,
    AnalysisSeverity Severity,
    AnalysisConfidence Confidence,
    string Title,
    string Message,
    string HelpUri,
    IReadOnlyList<CveWatchMatchRow> Matches);

/// <summary>
/// How a match becomes a finding (PLAN-004): the rule is the CVE, the fingerprint is the project, the CVE and
/// the package without its version, so a package bump that stays vulnerable is the same finding and a
/// decision taken on it stands. A match to verify is a low-confidence finding, never a fixed one.
/// </summary>
internal static class CveWatchFindings
{
    public const string ScannerKey = AcceptedRiskDuration.CveWatchScannerKey;
    public const string ScannerName = "CVE watch";
    public const string ScannerVersion = "1";

    public static AnalysisConfidence ConfidenceOf(VulnerabilityMatchStatus status) =>
        status == VulnerabilityMatchStatus.Affected ? AnalysisConfidence.High : AnalysisConfidence.Low;

    /// <summary>
    /// Container is what a project runs on: the packages of an image (Debian, Ubuntu, Alpine), a base image, and the
    /// language runtime itself, which is where the pipeline function <c>scan-dotnet-runtime-cve</c> already files the
    /// .NET runtime CVEs. Dependencies is what a project links against. The category only sorts the finding: the gate
    /// and the grade of a run never read the watch's reports.
    /// </summary>
    public static AnalysisCategory CategoryOf(string ecosystem) =>
        ecosystem.StartsWith("Debian", StringComparison.Ordinal)
        || ecosystem.StartsWith("Ubuntu", StringComparison.Ordinal)
        || ecosystem.StartsWith("Alpine", StringComparison.Ordinal)
        || ecosystem is RuntimeEcosystems.Runtime or RuntimeEcosystems.Docker
            ? AnalysisCategory.Container
            : AnalysisCategory.Dependencies;

    public static string FingerprintOf(int projectId, string ruleId, string ecosystem, string packageName) =>
        Hash($"{ScannerKey}|{projectId}|{ruleId}|{ecosystem}|{packageName}");

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The findings an open set of matches stands for, in a stable order.</summary>
    public static List<CveWatchFindingDraft> Draft(int projectId, IEnumerable<CveWatchMatchRow> matches) =>
        matches
            .GroupBy(match => (RuleId: match.PrimaryCveId ?? match.ExternalId, match.Ecosystem, match.PackageName))
            .Select(group => ToDraft(projectId, group.Key.RuleId, group.Key.Ecosystem, group.Key.PackageName, [.. group]))
            .OrderBy(draft => draft.Fingerprint, StringComparer.Ordinal)
            .ToList();

    private static CveWatchFindingDraft ToDraft(
        int projectId, string ruleId, string ecosystem, string packageName, List<CveWatchMatchRow> matches)
    {
        // One match affected is enough for the finding to be certain; the advisories of one CVE may rate it differently.
        var status = matches.Any(match => match.Status == VulnerabilityMatchStatus.Affected)
            ? VulnerabilityMatchStatus.Affected
            : VulnerabilityMatchStatus.ToVerify;
        var best = matches.OrderByDescending(match => match.Severity).ThenBy(match => match.ExternalId, StringComparer.Ordinal).First();
        var versions = string.Join(", ", matches.Select(match => match.Version).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var fixedVersion = matches.Select(match => match.FixedVersion).FirstOrDefault(version => version is not null);
        var verdict = status == VulnerabilityMatchStatus.Affected
            ? $"{packageName} {versions} is affected"
            : $"{packageName} {versions} could not be compared with the advisory";
        var message = $"{best.Summary ?? best.ExternalId}. {verdict}"
            + (fixedVersion is null ? "; no fixed version is stated." : $"; fixed in {fixedVersion}.");
        return new CveWatchFindingDraft(
            FingerprintOf(projectId, ruleId, ecosystem, packageName),
            ruleId,
            CategoryOf(ecosystem),
            best.Severity,
            ConfidenceOf(status),
            Truncate($"{ruleId} in {packageName}", 500),
            Truncate(message, 4000),
            VulnerabilityAdvisoryLinks.For(best.ExternalId, best.FixedVersion ?? fixedVersion),
            matches);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
