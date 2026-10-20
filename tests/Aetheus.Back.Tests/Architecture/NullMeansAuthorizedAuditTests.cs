// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Architectural guard against one specific, repeated defect: <b>an absent value granting access</b>.
///
/// The shape is <c>if (x is { } v &amp;&amp; !await authz.HasPermissionAsync(...)) return Forbid();</c>.
/// When <c>x</c> is null the <c>&amp;&amp;</c> short-circuits, the authorization call never runs, and
/// the guard does not close - it disappears. A 2026-08 security audit found this exact form behind
/// four of its seven findings, including two rated HIGH.
///
/// The form is not always wrong, which is why this is a whitelist rather than a ban. It is CORRECT
/// when null means "this optional resource was not named, so there is nothing to authorize", AND an
/// unconditional gate already protects the operation. It is a HOLE when null means "the owner is
/// somewhere else" - because that somewhere else is then never authorized at all.
///
/// A new occurrence fails this test. Closing it means either writing the refusal on the null branch,
/// or adding the site below with a comment that says which of the two cases it is. An entry whose
/// justification cannot be written is the defect, not a formality.
/// </summary>
public sealed partial class NullMeansAuthorizedAuditTests
{
    /// <summary>
    /// Sites where the null branch is examined and accounted for. Keyed <c>{RelPath}:{Method}</c>.
    /// Every entry MUST say why null needs no authorization, or record that it is a known gap.
    /// </summary>
    private static readonly HashSet<string> Reviewed = new(StringComparer.Ordinal)
    {
        // CORRECT. SourceEnvironmentId is the optional environment to duplicate FROM. Null means the
        // environment is created empty, so there is no source to hold Read on, and the unconditional
        // Environment/Write gate on the line above already protects the creation itself.
        "src/Aetheus.Back/Components/Environments/EnvironmentsController.cs:CreateEnvironment",

        // CORRECT. TemplateId is null when the pipeline extends no template, so there is no template
        // to hold Read on. The item itself is already gated by the unconditional Pipeline/Read above.
        "src/Aetheus.Back/Components/Pipelines/PipelineFleetController.cs:GetFleetItem",

        // Pipeline owner authorization checks each nullable owner branch explicitly through
        // PipelineOwnerAuthorization.CanOwnAsync.
    };

    /// <summary>A null-tolerant pattern match: <c>x is { } name</c>.</summary>
    [GeneratedRegex(@"is\s*\{\s*\}\s*\w+", RegexOptions.CultureInvariant)]
    private static partial Regex NullTolerantMatch();

    /// <summary>
    /// A NEGATED authorization call. The negation is what matters: <c>!await authz...</c> inside an
    /// <c>&amp;&amp;</c> chain is the branch that writes the refusal, so short-circuiting past it is
    /// what removes the protection. Lease and ownership probes are not authorization and are not
    /// matched.
    /// </summary>
    [GeneratedRegex(
        @"!\s*await\s+[\w.]*(authz|authorization)[\w.]*\.\w+|!\s*await\s+\w*\.?(HasPermissionAsync|HasAnyPermissionAsync|CanAccessAllAsync)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NegatedAuthorizationCall();

    [Fact]
    public void NoAuthorizationCondition_LetsANullValueSkipTheCheck()
    {
        var repoRoot = RepositoryScan.Root;
        var backend = Path.Combine(repoRoot, "src", "Aetheus.Back");
        var offenders = new List<string>();

        foreach (var file in RepositoryScan.Enumerate(backend, "*.cs"))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;

            var text = File.ReadAllText(file);
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

            foreach (var condition in Conditions(text))
            {
                if (!NullTolerantMatch().IsMatch(condition.Text)) continue;
                if (!condition.Text.Contains("&&", StringComparison.Ordinal)) continue;
                if (!NegatedAuthorizationCall().IsMatch(condition.Text)) continue;

                var key = $"{rel}:{EnclosingMethod(text, condition.Start)}";
                if (Reviewed.Contains(key)) continue;
                offenders.Add(key);
            }
        }

        Assert.True(offenders.Count == 0,
            "An absent value must never grant access. These conditions short-circuit past their "
            + "authorization call when the matched value is null, so the guard disappears instead of "
            + "closing. Write the refusal on the null branch, or add the site to Reviewed with a "
            + $"comment saying why null needs no authorization:{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders.Distinct().Order().Select(entry => $"  - {entry}")));
    }

    /// <summary>
    /// Proof that the scan above can fail. A whitelist-driven guard whose detector silently stops
    /// matching - a refactor to <c>is not null</c>, a renamed authorization service - would go green
    /// forever while the defect it exists for came back. These samples pin the detector to the exact
    /// shapes the audit found, including the four-line formatting every real occurrence uses.
    /// </summary>
    [Theory]
    // The verbatim shape of F-003, the finding rated HIGH.
    [InlineData("if (artifact.ProjectId is { } projectId\n && !await authz.HasPermissionAsync(User, ResourceType.Project, projectId, permission, ct))\n return (null, Forbid());")]
    // Same defect through a differently named authorization collaborator.
    [InlineData("if (item.TemplateId is { } id && !await authorization.CanAccessAllAsync(User, id, ct)) return Forbid();")]
    public void Detector_MatchesTheAntiPattern(string source)
    {
        var condition = Assert.Single(Conditions(source));
        Assert.Matches(NullTolerantMatch(), condition.Text);
        Assert.Matches(NegatedAuthorizationCall(), condition.Text);
    }

    [Theory]
    // Fail-closed: the null branch refuses on its own line, so nothing is short-circuited past.
    [InlineData("if (owningProjectId is not { } projectId) return (null, Forbid());")]
    // An unconditional check, the shape the fixes above converge on.
    [InlineData("if (!await authz.HasPermissionAsync(User, ResourceType.Project, projectId, permission, ct)) return Forbid();")]
    // A lease probe is not authorization: matching it would drown the guard in false positives.
    [InlineData("if (result.Token is { } token && !await repository.HasCurrentAgentLeaseAsync(id, token, ct)) return;")]
    public void Detector_DoesNotMatchCorrectCode(string source)
    {
        var condition = Assert.Single(Conditions(source));
        var flagged = NullTolerantMatch().IsMatch(condition.Text)
            && condition.Text.Contains("&&", StringComparison.Ordinal)
            && NegatedAuthorizationCall().IsMatch(condition.Text);
        Assert.False(flagged);
    }

    /// <summary>
    /// The parenthesised condition of every <c>if</c>, with newlines flattened so a condition split
    /// over four lines - which every real occurrence is - reads as one string. Parentheses are
    /// balanced rather than matched by regex, because the calls inside carry their own.
    /// </summary>
    private static IEnumerable<(string Text, int Start)> Conditions(string text)
    {
        foreach (Match match in Regex.Matches(text, @"\bif\s*\(", RegexOptions.CultureInvariant))
        {
            var open = match.Index + match.Length - 1;
            var depth = 0;
            for (var index = open; index < text.Length; index++)
            {
                if (text[index] == '(') depth++;
                else if (text[index] == ')')
                {
                    depth--;
                    if (depth != 0) continue;
                    var condition = text[(open + 1)..index];
                    yield return (Whitespace().Replace(condition, " "), match.Index);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Name of the method a condition sits in, used to key the whitelist on something stable: line
    /// numbers move with every edit, so a whitelist keyed on them would silently stop matching.
    /// </summary>
    private static string EnclosingMethod(string text, int position)
    {
        var declarations = MethodDeclaration().Matches(text[..position]);
        return declarations.Count == 0 ? "(unknown)" : declarations[^1].Groups["name"].Value;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(
        @"(?:public|private|protected|internal)\s+(?:static\s+|async\s+|sealed\s+|override\s+|virtual\s+)*[\w<>,\[\]\?\.\s]+?\s(?<name>\w+)\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex MethodDeclaration();
}
