// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the <c>record</c> half of the former <c>list-failed-tests.mjs</c> script, with the cases
/// of <c>list-failed-tests.test.mjs</c> that apply to it: which outcomes count, entity decoding, the
/// one-line safety rules, and the bounded value the pipeline publishes.
/// </summary>
public sealed class FailedTestNamesTests
{
    internal static string Trx(params (string Name, string Outcome)[] results)
    {
        var failed = results.Count(result => result.Outcome == "Failed");
        var rows = string.Concat(results.Select(result =>
            $"<UnitTestResult executionId=\"x\" testId=\"y\" testName=\"{result.Name}\" computerName=\"qa\" outcome=\"{result.Outcome}\" duration=\"00:00:01\" />"));
        return $"<?xml version=\"1.0\" encoding=\"utf-8\"?><TestRun><Results>{rows}</Results>"
               + $"<ResultSummary outcome=\"{(failed > 0 ? "Failed" : "Completed")}\"><Counters total=\"{results.Length}\" executed=\"{results.Length}\" "
               + $"passed=\"{results.Length - failed}\" failed=\"{failed}\" error=\"0\" timeout=\"0\" aborted=\"0\" disconnected=\"0\" notExecuted=\"0\" />"
               + "</ResultSummary></TestRun>";
    }

    [Fact]
    public void ListsTheFailedErroredAndTimedOutTestsInOrderAndOnceEach()
    {
        var names = FailedTestNames.FromTrx(Trx(
            ("Suite.Passes", "Passed"),
            ("Suite.Fails", "Failed"),
            ("Suite.Skipped", "NotExecuted"),
            ("Suite.Errors", "Error"),
            ("Suite.Fails", "Failed"),
            ("Suite.TimesOut", "Timeout"),
            ("Suite.Aborts", "Aborted")));

        Assert.Equal(["Suite.Fails", "Suite.Errors", "Suite.TimesOut", "Suite.Aborts"], names);
    }

    [Fact]
    public void DecodesXmlEntitiesAndKeepsAParameterizedNameOnOneCommaFreeLine()
    {
        var names = FailedTestNames.FromTrx(Trx(("Suite.Case(a: &quot;x, y&quot;, b: &lt;1&gt;)&#xA;next", "Failed")));

        Assert.Equal(["Suite.Case(a: \"x; y\"; b: <1>) next"], names);
    }

    [Fact]
    public void NeutralisesShellActiveCharacters() =>
        Assert.Equal("Run _(rm -rf) _x_", FailedTestNames.Sanitize("Run $(rm -rf) `x`"));

    [Fact]
    public void CollapsesWhitespaceAndBoundsTheLength()
    {
        Assert.Equal("a b c", FailedTestNames.Sanitize(" a\t b\u0001 c "));
        var bounded = FailedTestNames.Sanitize(new string('N', 250));
        Assert.Equal(200, bounded.Length);
        Assert.EndsWith("...", bounded, StringComparison.Ordinal);
    }

    [Fact]
    public void CapsTheValueToTheFirstNamesWithAPlusNMoreTail()
    {
        var names = Enumerable.Range(0, FailedTestNames.MaxNames + 7).Select(index => $"Suite.Test{index}").ToList();

        var value = FailedTestNames.Format(names);

        Assert.StartsWith("Suite.Test0, Suite.Test1", value, StringComparison.Ordinal);
        Assert.EndsWith(", +7 more", value, StringComparison.Ordinal);
        Assert.Equal(FailedTestNames.MaxNames + 1, value.Split(", ").Length);
    }

    [Fact]
    public void CapsTheValueLengthEvenWhenTheNamesAreLong()
    {
        var names = Enumerable.Range(0, 15).Select(index => new string('N', 190) + index).ToList();

        var value = FailedTestNames.Format(names);

        Assert.True(value.Length <= FailedTestNames.MaxValueLength, $"length {value.Length}");
        Assert.Matches(@"\+\d+ more$", value);
    }

    [Fact]
    public void LogLine_CountsEveryName() =>
        Assert.Equal("Failed tests (2): A.One, A.Two", FailedTestNames.LogLine(["A.One", "A.Two"]));
}
