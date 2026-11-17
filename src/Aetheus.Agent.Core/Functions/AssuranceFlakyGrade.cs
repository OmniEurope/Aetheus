// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The grade a candidate's flaky tests give it (owner decision of 2026-11-05, PLAN-006): a test that
/// failed on one attempt and passed on its single rerun is a named finding, never a failure, and the
/// candidate is graded on how many distinct such tests all its suites sealed together: none is A, 1 to
/// 10 is B, 11 to 20 is C, 21 or more is F. A test that failed on both attempts is not counted here: it
/// fails its suite, which is graded F as it always was.
///
/// The count is read back from the sealed <c>flakyTests</c> values, the one-line lists the pipelines
/// published (<see cref="FailedTestNames.Format"/>): names separated by <c>", "</c> (a sanitized name
/// carries no comma), the list cut at <see cref="FailedTestNames.MaxNames"/> with a <c>+N more</c>
/// tail. A tail counts its N tests, which may repeat a name another suite listed: the count can only err
/// towards a worse grade, never a better one.
/// </summary>
internal static partial class AssuranceFlakyGrade
{
    /// <summary>The highest count each grade allows; above the last, F.</summary>
    private static readonly (int Max, string Grade)[] Bands = [(0, "A"), (10, "B"), (20, "C")];

    [GeneratedRegex("^\\+([1-9][0-9]*) more\\z")]
    private static partial Regex MoreTail();

    /// <summary>The grade of <paramref name="count"/> distinct flaky tests.</summary>
    public static string Grade(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        foreach (var (max, grade) in Bands)
            if (count <= max)
                return grade;
        return "F";
    }

    /// <summary>The distinct flaky tests of the sealed lists, each one-line value of a suite.</summary>
    public static int Count(IEnumerable<string> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var untold = 0L;
        foreach (var list in lists)
            foreach (var entry in list.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var tail = MoreTail().Match(entry);
                if (tail.Success && long.TryParse(tail.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var more))
                    untold += more;
                else
                    names.Add(entry);
            }
        return (int)Math.Min(int.MaxValue, names.Count + untold);
    }
}
