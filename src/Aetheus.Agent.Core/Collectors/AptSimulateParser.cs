// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>
/// Parses <c>apt-get -s upgrade</c> output (ADR-024 4.1). apt emits one machine-readable
/// <c>Inst &lt;pkg&gt; [&lt;current&gt;] (&lt;candidate&gt; &lt;origin...&gt;)</c> line per package that WOULD be
/// upgraded; the origin carries the archive suite (e.g. <c>noble-security</c>), which flags security
/// updates. Pure and shell-free so both the collector and the upgrade executor can reuse it and it is
/// unit-testable without an apt box.
/// </summary>
public static partial class AptSimulateParser
{
    [GeneratedRegex(@"^Inst\s+(?<pkg>\S+)\s+(?:\[(?<cur>[^\]]*)\]\s+)?\((?<cand>\S+)\s+(?<origin>[^)]*)\)",
        RegexOptions.Multiline)]
    private static partial Regex InstLineRegex();

    public static IReadOnlyList<PendingUpdateDto> Parse(string aptSimulateOutput)
    {
        if (string.IsNullOrEmpty(aptSimulateOutput)) return [];
        var list = new List<PendingUpdateDto>();
        foreach (Match m in InstLineRegex().Matches(aptSimulateOutput))
        {
            var origin = m.Groups["origin"].Value;
            list.Add(new PendingUpdateDto
            {
                Package = m.Groups["pkg"].Value,
                CurrentVersion = m.Groups["cur"].Success ? m.Groups["cur"].Value : string.Empty,
                CandidateVersion = m.Groups["cand"].Value,
                IsSecurity = origin.Contains("security", StringComparison.OrdinalIgnoreCase)
            });
        }
        return list;
    }

    /// <summary>
    /// The distinct blocked packages among <paramref name="pending"/>: those on the shared
    /// <see cref="CriticalPackages"/> blocklist or in the optional per-server <paramref name="extraBlocklist"/>.
    /// A non-empty result means the whole upgrade must be aborted honestly (never a fake success).
    /// </summary>
    public static IReadOnlyList<string> BlockedPackages(
        IEnumerable<PendingUpdateDto> pending, IEnumerable<string>? extraBlocklist = null)
    {
        var extra = new HashSet<string>(extraBlocklist ?? [], StringComparer.OrdinalIgnoreCase);
        return pending
            .Where(p => CriticalPackages.IsCritical(p.Package) || extra.Contains(p.Package))
            .Select(p => p.Package)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
