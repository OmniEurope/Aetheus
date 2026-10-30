// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// R-113 (rule 6: everything the user reads is French). A warning written on a pipeline run is read on the run
/// page, in the user's language, by the front: it can only do so for a stored problem (a code, its parameters and
/// the English sentence, <c>PipelineProblem.ToStored()</c>), the step-failure wrappers built on it, or a run notice.
/// A call that writes a sentence as a plain string literal to <c>AppendRunWarningsAsync</c> would be shown in
/// English to a French user, which is the defect this guards.
///
/// The warnings that come from elsewhere than a literal (the free text a step publishes in its
/// <c>RUN_WARNING</c> output, the reasons the stage dispatch planner collects) are variables, not literals, and are
/// not seen by this scan: they are listed as remaining in the R-113 report, not accepted here.
/// </summary>
public sealed class RunWarningStoredProblemAuditTests
{
    private static readonly Regex Call = new(@"\bAppendRunWarningsAsync\s*\(", RegexOptions.Compiled);

    private static readonly string[] StoredForms =
        [".ToStored(", "PipelineStepFailureWarning.", "PipelineRunNotices.Compose(", "StoredTogether("];

    [Fact]
    public void NoRunWarning_IsWrittenAsAPlainEnglishLiteral()
    {
        var directory = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Back", "Components", "Pipelines");
        var offenders = new List<string>();
        var calls = 0;

        foreach (var file in RepositoryScan.Enumerate(directory, "*.cs"))
        {
            var (fileCalls, lines) = Scan(File.ReadAllText(file));
            calls += fileCalls;
            offenders.AddRange(lines.Select(line => $"{Path.GetRelativePath(RepositoryScan.Root, file)}:{line}"));
        }

        Assert.True(calls >= 15, $"Only {calls} run-warning writes were found: the scan is broken.");
        Assert.True(offenders.Count == 0,
            "A run warning is written as a plain string literal; store it as a problem (PipelineProblem.Of(code, english, ...).ToStored()) "
            + "and add its resources LaunchProblem_{code} in AppStrings.resx and AppStrings.fr-FR.resx:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheScan_FlagsALiteralWarning_AndAcceptsStoredOnes()
    {
        const string literal = "await repo.AppendRunWarningsAsync(id, [\"Stage 'x' failed (see logs); stopped.\"], ct);";
        const string interpolated = "await repo.AppendRunWarningsAsync(id, [$\"Step '{name}': {reason}\"], ct);";
        const string stored = "await repo.AppendRunWarningsAsync(id, [PipelineProblem.Of(C, \"A sentence; (with parentheses)\").ToStored()], ct);";
        const string wrapper = "await repo.AppendRunWarningsAsync(id, [PipelineStepFailureWarning.Deploy(step, reason)], ct);";
        const string variable = "await repo.AppendRunWarningsAsync(id, reasons, ct);";

        Assert.Equal([1], Scan(literal).Offenders);
        Assert.Equal([1], Scan(interpolated).Offenders);
        Assert.Empty(Scan(stored).Offenders);
        Assert.Empty(Scan(wrapper).Offenders);
        Assert.Empty(Scan(variable).Offenders);
        Assert.All(new[] { literal, interpolated, stored, wrapper, variable }, source => Assert.Equal(1, Scan(source).Calls));
    }

    /// <summary>The number of writes in <paramref name="source"/> and the lines of those that pass a literal.</summary>
    private static (int Calls, int[] Offenders) Scan(string source)
    {
        var calls = 0;
        var offenders = new List<int>();
        foreach (Match match in Call.Matches(source))
        {
            if (IsDeclaration(source, match.Index)) continue;
            calls++;
            var statement = source[match.Index..CallEnd(source, match.Index + match.Length)];
            if (statement.Contains('"') && !StoredForms.Any(form => statement.Contains(form, StringComparison.Ordinal)))
                offenders.Add(LineOf(source, match.Index));
        }

        return (calls, [.. offenders]);
    }

    /// <summary>The index of the parenthesis closing the call whose arguments start at <paramref name="start"/>;
    /// a parenthesis inside a string literal (a sentence) does not count.</summary>
    private static int CallEnd(string source, int start)
    {
        var depth = 1;
        for (var index = start; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '"':
                    index++;
                    while (index < source.Length && source[index] != '"')
                        index += source[index] == '\\' ? 2 : 1;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0) return index;
                    break;
            }
        }

        throw new InvalidOperationException("A call to AppendRunWarningsAsync has no closing parenthesis: the scan is broken.");
    }

    private static bool IsDeclaration(string source, int index)
    {
        var lineStart = source.LastIndexOf('\n', index) + 1;
        var prefix = source[lineStart..index];
        return prefix.Contains("Task ", StringComparison.Ordinal) || prefix.Contains("=>", StringComparison.Ordinal);
    }

    private static int LineOf(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;
}
