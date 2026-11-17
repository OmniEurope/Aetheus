// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Analysis;

public sealed record ParsedAnalysisFinding
{
    public string Fingerprint { get; init; } = string.Empty;
    public string LocationHash { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public string RuleId { get; init; } = string.Empty;
    public AnalysisCategory Category { get; init; }
    public AnalysisSeverity Severity { get; init; }
    public AnalysisConfidence Confidence { get; init; }
    public string? Cwe { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? HelpUri { get; init; }
    public string? FilePath { get; init; }
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public string? Symbol { get; init; }

    /// <summary>The fingerprint a vulnerability finding had before it was identified by vulnerability and package
    /// (<see cref="VulnerabilityIdentity"/>): a finding stored under it is adopted, keeping its decisions. Empty for
    /// any other finding.</summary>
    public string LegacyFingerprint { get; init; } = string.Empty;

    /// <summary>The fingerprints the same vulnerability would carry under its other identifiers (the GHSA of a CVE),
    /// to find a finding another scanner stored under an alias. Empty when the scanner gave no alias.</summary>
    public IReadOnlyList<string> AlternateFingerprints { get; init; } = [];
}
