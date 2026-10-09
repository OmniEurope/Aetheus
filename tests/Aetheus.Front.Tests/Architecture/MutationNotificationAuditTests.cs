// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// User-triggered API mutations must produce visible notification feedback on success. HTTP failures
/// are also covered globally by ErrorNotificationHandler, while result-driven failures can add a
/// local error toast. This guard prevents new silent actions from reaching any Razor page.
/// </summary>
public sealed class MutationNotificationAuditTests
{
    private static readonly Regex MethodStart = new(
        @"(?m)^\s*(?:private|protected|internal|public)\s+(?:static\s+)?(?:async\s+)?"
        + @"[\w<>\?\[\],\.]+\s+(?<name>\w+)\s*\(",
        RegexOptions.Compiled);

    // The optional middle segment matches the per-domain sub-clients (Api.Servers.DeleteXAsync) as
    // well as the flat form. Without it this audit would stop seeing the mutations it exists to check.
    private static readonly Regex MutationCall = new(
        @"Api\.(?:\w+\.)?(?:Create|Update|Delete|Remove|Add|Change|Set|Revoke|Regenerate|Cancel|Retry|Run|"
        + @"Execute|Start|Stop|Save|Upload|Enable|Disable|Install|Uninstall|Approve|Reject|"
        + @"Acknowledge|Resolve|Import|Clone|Assign|Unassign|Test|Trigger|Sync|Move|Verify|Setup|"
        + @"Deploy|Edit|Register|Unregister|Generate|Apply|Rotate|Request)\w*Async\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex NotificationFeedback = new(
        @"(?:Toast|Notify)\.(?:Success|Info|Warning|Notify)\s*\("
        + @"|Notifications?\.Notify\s*\("
        + @"|Ui\.RunAsync\s*\("
        + @"|Clipboard\.CopyAsync\s*\("
        + @"|HandleSaveOutcome\s*\("
        + @"|ExecuteServerActionAsync\s*\("
        + @"|RunOperationAsync\s*\(",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, string> DelegatedFeedback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Components/Audit/AuditDetailDialog.razor.cs:OnInitializedAsync"] =
            "passive integrity read rendered directly in the opened detail dialog",
        ["Components/Servers/ServerDetailSections/ServerServicesSection.razor.cs:StartLogStreamAsync"] =
            "opens a read-only task-log stream; it does not mutate server state",
        ["Components/Shared/QualityGatePolicyEditor.razor.cs:SaveRequestAsync"] =
            "transport helper; SaveAsync owns success feedback",
        ["Components/Shared/QualityGatePolicyEditor.razor.cs:SaveBatchAsync"] =
            "transport helper; preset/import callers own success feedback"
    };

    [Fact]
    public void UserFacingMutations_HaveNotificationFeedback()
    {
        var frontDir = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        var roots = RepositoryScan.PageRoots;
        var violations = new List<string>();
        var usedDelegations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mutationCount = 0;

        foreach (var (_, file) in RepositoryScan.EnumerateUnion(roots, "*.razor.cs"))
        {
            var source = File.ReadAllText(file);
            var relative = Path.GetRelativePath(frontDir, file).Replace('\\', '/');
            foreach (var method in EnumerateMethods(source))
            {
                if (!MutationCall.IsMatch(method.Body)) continue;
                mutationCount++;
                if (NotificationFeedback.IsMatch(method.Body)) continue;

                var key = $"{relative}:{method.Name}";
                if (DelegatedFeedback.ContainsKey(key))
                {
                    usedDelegations.Add(key);
                    continue;
                }
                violations.Add($"{key} calls a mutating API without notification feedback");
            }
        }

        Assert.True(mutationCount >= 100, $"Only {mutationCount} mutation handlers were audited.");
        Assert.Empty(violations);
    }

    private static IEnumerable<(string Name, string Body)> EnumerateMethods(string source)
    {
        foreach (Match match in MethodStart.Matches(source))
        {
            var signatureEnd = source.IndexOf(')', match.Index + match.Length);
            if (signatureEnd < 0) continue;
            var brace = source.IndexOf('{', signatureEnd);
            var arrow = source.IndexOf("=>", signatureEnd, StringComparison.Ordinal);
            if (arrow >= 0 && (brace < 0 || arrow < brace))
            {
                var semicolon = source.IndexOf(';', arrow);
                if (semicolon >= 0)
                    yield return (match.Groups["name"].Value, source[match.Index..(semicolon + 1)]);
                continue;
            }
            if (brace < 0) continue;
            var end = FindMatchingBrace(source, brace);
            if (end >= 0)
                yield return (match.Groups["name"].Value, source[match.Index..(end + 1)]);
        }
    }

    private static int FindMatchingBrace(string source, int open)
    {
        var depth = 0;
        var inString = false;
        var inChar = false;
        var inLineComment = false;
        var inBlockComment = false;
        for (var i = open; i < source.Length; i++)
        {
            var current = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (ConsumeComment(
                    current, next, ref i, ref inLineComment, ref inBlockComment, inString, inChar))
                continue;
            if (ToggleQuote(source, i, ref inString, ref inChar)) continue;
            if (inString || inChar) continue;
            if (current == '{') depth++;
            else if (current == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// An unescaped quote opens or closes a string or char literal (one kind cannot open inside the
    /// other); true when the character at <paramref name="i"/> was such a quote. Recette R2-107: moved out
    /// of <see cref="FindMatchingBrace"/>, under the grade-A cyclomatic threshold.
    /// </summary>
    private static bool ToggleQuote(string source, int i, ref bool inString, ref bool inChar)
    {
        var current = source[i];
        var escaped = i > 0 && source[i - 1] == '\\';
        if (!inChar && current == '"' && !escaped) { inString = !inString; return true; }
        if (!inString && current == '\'' && !escaped) { inChar = !inChar; return true; }
        return false;
    }

    private static bool ConsumeComment(
        char current,
        char next,
        ref int index,
        ref bool inLineComment,
        ref bool inBlockComment,
        bool inString,
        bool inChar)
    {
        if (inLineComment)
        {
            if (current == '\n') inLineComment = false;
            return true;
        }
        if (inBlockComment)
        {
            if (current == '*' && next == '/') { inBlockComment = false; index++; }
            return true;
        }
        if (inString || inChar || current != '/') return false;
        if (next == '/') { inLineComment = true; index++; return true; }
        if (next == '*') { inBlockComment = true; index++; return true; }
        return false;
    }

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
