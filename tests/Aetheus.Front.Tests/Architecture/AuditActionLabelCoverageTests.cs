// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Audit action literals and supported interpolated families require labels in both
/// cultures. Unsupported families fail the guard; variable-held actions remain outside this source
/// scan.</summary>
public sealed class AuditActionLabelCoverageTests
{
    private static readonly Regex LogCall = new(@"\b\w*[Aa]udit\w*\s*\.\s*LogAsync\s*\(", RegexOptions.Compiled);
    private static readonly Regex Literal = new(@"""(?<value>[A-Za-z][A-Za-z.]*)""", RegexOptions.Compiled);
    private static readonly Regex LoginFailureReason = new(
        @"RecordFailureAsync\(\s*[^,]+,\s*[^,]+,\s*""(?<reason>\w+)""", RegexOptions.Compiled);

    /// <summary>The interpolated audit actions and the values their placeholder takes.</summary>
    private static readonly Dictionary<string, IEnumerable<string>> Families = new(StringComparer.Ordinal)
    {
        ["$\"Apache{request.Action}\""] = Enum.GetNames<ApacheAction>().Select(value => "Apache" + value),
        ["$\"Certbot{request.Action}\""] = Enum.GetNames<CertbotAction>().Select(value => "Certbot" + value),
        ["$\"Docker{request.Action}\""] = Enum.GetNames<DockerContainerAction>().Select(value => "Docker" + value),
        ["$\"Firewall{operation}\""] = new[] { OperationKind.FirewallAllow, OperationKind.FirewallDeny, OperationKind.FirewallDeleteRule }
            .Select(value => "Firewall" + value),
        ["$\"Mail{request.Action}\""] = Enum.GetNames<MailAction>().Select(value => "Mail" + value),
        ["$\"Portsentry{request.Action}\""] = Enum.GetNames<PortsentryAction>().Select(value => "Portsentry" + value),
        ["$\"Rkhunter{request.Action}\""] = Enum.GetNames<RkhunterAction>().Select(value => "Rkhunter" + value),
        ["$\"Service{request.Action}\""] = Enum.GetNames<ServiceAction>().Select(value => "Service" + value),
        ["$\"Teamspeak{request.Action}\""] = Enum.GetNames<TeamspeakAction>().Select(value => "Teamspeak" + value),
        // Filled from the reasons LoginAuditRecorder.RecordFailureAsync is called with.
        ["$\"LoginFailed.{reason}\""] = []
    };

    [Fact]
    public void EveryAuditActionTheBackendWrites_HasALabelInBothCultures()
    {
        var backend = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Back");
        var actions = new SortedSet<string>(StringComparer.Ordinal);
        var unknownFamilies = new List<string>();
        foreach (var file in RepositoryScan.Enumerate(backend, "*.cs"))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            foreach (Match reason in LoginFailureReason.Matches(text))
                actions.Add("LoginFailed." + reason.Groups["reason"].Value);
            foreach (Match call in LogCall.Matches(text))
            {
                var argument = FirstArgument(text, call.Index + call.Length);
                if (argument.StartsWith("$\"", StringComparison.Ordinal))
                {
                    if (Families.TryGetValue(argument, out var values)) actions.UnionWith(values);
                    else unknownFamilies.Add($"{Path.GetFileName(file)}: {argument}");
                    continue;
                }
                foreach (Match literal in Literal.Matches(argument))
                    actions.Add(literal.Groups["value"].Value);
            }
        }

        Assert.True(actions.Count > 100, $"Only {actions.Count} audit actions read from the backend: the scan is broken.");
        Assert.True(unknownFamilies.Count == 0,
            "Action d'audit interpolée inconnue : étendre Families avec ses valeurs.\n  " + string.Join("\n  ", unknownFamilies));

        var english = Keys("AppStrings.resx");
        var french = Keys("AppStrings.fr-FR.resx");
        var missing = new StringBuilder();
        foreach (var action in actions)
        {
            var key = "AuditAction_" + action;
            if (!english.Contains(key)) missing.Append("\n  EN ").Append(key);
            if (!french.Contains(key)) missing.Append("\n  FR ").Append(key);
        }

        Assert.True(missing.Length == 0, "Action d'audit sans libellé (recette R-452) :" + missing);
    }

    /// <summary>The first argument of a call, from just after its opening parenthesis to the first
    /// top-level comma (strings and nested brackets skipped).</summary>
    private static string FirstArgument(string text, int start)
    {
        var builder = new StringBuilder();
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                builder.Append(c);
                if (c == '\\' && i + 1 < text.Length) builder.Append(text[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; builder.Append(c); continue; }
            if (c is '(' or '[' or '{') depth++;
            if (c is ')' or ']' or '}')
            {
                if (depth == 0) break;
                depth--;
            }
            if (c == ',' && depth == 0) break;
            builder.Append(c);
        }
        var argument = Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
        return argument.StartsWith("action:", StringComparison.Ordinal) ? argument["action:".Length..].Trim() : argument;
    }

    private static HashSet<string> Keys(string resource) =>
        XDocument.Load(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Resources", resource))
            .Root!.Elements("data")
            .Select(element => (string)element.Attribute("name")!)
            .ToHashSet(StringComparer.Ordinal);
}
