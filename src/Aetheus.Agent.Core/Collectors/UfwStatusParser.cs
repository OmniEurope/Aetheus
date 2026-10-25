// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Collectors;

/// <summary>
/// Parses <c>ufw status numbered</c> output (ADR-024 4.2). Pure + shell-free so the firewall collector
/// can reuse it and it is unit-testable without a ufw box. Output shape:
/// <code>
/// Status: active
///
///      To                         Action      From
///      --                         ------      ----
/// [ 1] 22/tcp                     ALLOW IN    Anywhere
/// [ 2] 8080/tcp                   ALLOW IN    10.0.0.0/8
/// </code>
/// </summary>
public static partial class UfwStatusParser
{
    [GeneratedRegex(@"^Status:\s*active", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ActiveRegex();

    // [ 1] 22/tcp                ALLOW IN    Anywhere
    // [ 2] 22/tcp (v6)           ALLOW IN    Anywhere (v6)   <- IPv6 lines carry a "(v6)" qualifier after <to>
    [GeneratedRegex(@"^\[\s*(?<num>\d+)\]\s+(?<to>\S+)(?:\s+\(v6\))?\s+(?<action>ALLOW|DENY|REJECT|LIMIT)\s+\S+\s+(?<from>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex RuleRegex();

    public static bool IsActive(string ufwStatusOutput)
        => !string.IsNullOrEmpty(ufwStatusOutput) && ActiveRegex().IsMatch(ufwStatusOutput);

    public static IReadOnlyList<FirewallRuleDto> ParseRules(string ufwStatusOutput)
    {
        if (string.IsNullOrEmpty(ufwStatusOutput)) return [];
        var rules = new List<FirewallRuleDto>();
        foreach (Match m in RuleRegex().Matches(ufwStatusOutput))
        {
            var to = m.Groups["to"].Value;              // e.g. "22/tcp", "80", "22"
            var slash = to.IndexOf('/');
            int? port = null;
            var proto = "any";
            if (slash > 0)
            {
                if (int.TryParse(to[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) port = p;
                proto = to[(slash + 1)..];
            }
            else if (int.TryParse(to, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
            {
                port = p;
            }

            rules.Add(new FirewallRuleDto
            {
                Number = int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture),
                Action = m.Groups["action"].Value.ToLowerInvariant(),
                Port = port,
                Protocol = proto,
                Source = m.Groups["from"].Value.Trim(),
                Raw = m.Value.Trim()
            });
        }
        return rules;
    }
}
