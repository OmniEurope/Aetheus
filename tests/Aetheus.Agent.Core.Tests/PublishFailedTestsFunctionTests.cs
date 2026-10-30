// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the former <c>publish-failed-tests.sh</c> script and of <c>list-failed-tests.mjs</c>,
/// with the cases of <c>list-failed-tests.test.mjs</c> and of the publication half of
/// <c>failed-tests-publication.test.mjs</c>: the failed names of TRX files and lists, made safe for one
/// setvariable line, published under the variable the step names, and never a failed step.
/// </summary>
public sealed class PublishFailedTestsFunctionTests : IDisposable
{
    private readonly FunctionTestHarness _harness = new("aetheus-failed-tests");
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);

    public void Dispose() => _harness.Dispose();

    private static string Trx(params (string Name, string Outcome)[] results)
    {
        var rows = results.Select(result =>
            $"<UnitTestResult executionId=\"x\" testId=\"y\" testName=\"{result.Name}\" computerName=\"qa\" outcome=\"{result.Outcome}\" duration=\"00:00:01\" />");
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?><TestRun><Results>" + string.Concat(rows) + "</Results>"
               + $"<ResultSummary outcome=\"Failed\"><Counters total=\"{results.Length}\" executed=\"{results.Length}\" /></ResultSummary></TestRun>";
    }

    [Fact]
    public void Trx_ListsFailedErroredAndTimedOutTestsInOrderOnceEach()
    {
        var names = FailedTestNames.FromTrx(Trx(
            ("Suite.Passes", "Passed"), ("Suite.Fails", "Failed"), ("Suite.Skipped", "NotExecuted"),
            ("Suite.Errors", "Error"), ("Suite.Fails", "Failed"), ("Suite.TimesOut", "Timeout")));

        Assert.Equal(["Suite.Fails", "Suite.Errors", "Suite.TimesOut"], names);
    }

    [Fact]
    public void Trx_DecodesEntitiesAndKeepsAParameterizedNameOnOneCommaFreeLine() =>
        Assert.Equal(
            ["Suite.Case(a: \"x; y\"; b: <1>) next"],
            FailedTestNames.FromTrx(Trx(("Suite.Case(a: &quot;x, y&quot;, b: &lt;1&gt;)&#xA;next", "Failed"))));

    [Fact]
    public void Sanitize_NeutralisesShellActiveCharacters() =>
        Assert.Equal("Run _(rm -rf) _x_ a_b", FailedTestNames.Sanitize("Run $(rm -rf) `x` a\\b"));

    [Fact]
    public void Sanitize_BoundsALongName()
    {
        var name = FailedTestNames.Sanitize(new string('N', 250));

        Assert.Equal(200, name.Length);
        Assert.EndsWith("...", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_CapsTheValueToTheFirstNamesWithAMoreTail()
    {
        var names = Enumerable.Range(0, FailedTestNames.MaxNames + 7).Select(index => $"Suite.Test{index}").ToList();

        var value = FailedTestNames.Format(names);

        Assert.StartsWith("Suite.Test0, Suite.Test1", value, StringComparison.Ordinal);
        Assert.EndsWith(", +7 more", value, StringComparison.Ordinal);
        Assert.Equal(FailedTestNames.MaxNames + 1, value.Split(", ").Length);
    }

    [Fact]
    public void Format_CapsTheValueLengthEvenWhenTheNamesAreLong()
    {
        var names = Enumerable.Range(0, 15).Select(index => new string('N', 190) + index).ToList();

        var value = FailedTestNames.Format(names);

        Assert.True(value.Length <= FailedTestNames.MaxValueLength, $"length {value.Length}");
        Assert.Matches("\\+\\d+ more$", value);
    }

    [Fact]
    public void Collect_MergesTrxAndListSourcesAndSkipsAnAbsentOne()
    {
        var trx = _harness.Write("ws/integration.trx", Trx(("A.One", "Failed"), ("A.Two", "Passed")));
        var list = _harness.Write("ws/failed-tests.txt", "Deployed QA probes (current)\r\nA.One\n");

        Assert.Equal(
            ["A.One", "Deployed QA probes (current)"],
            FailedTestNames.Collect([trx, list, Path.Combine(_harness.Root, "absent-failed-tests.txt")]));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", true)]
    [InlineData("0 0 0", true)]
    [InlineData("0 1 0", false)]
    [InlineData("1", false)]
    public void Statuses_AreCleanOnlyWhenEveryOneIsZero(string? statuses, bool clean) =>
        Assert.Equal(clean, PublishFailedTestsFunction.AllClean(statuses));

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new PublishFailedTestsFunction(), _env);
    }

    [Fact]
    public async Task Publish_EmitsTheNamedVariableNextToTheLogLine()
    {
        _harness.Write("ws/coverage/analyzers/analyzers.trx", Trx(("A.One", "Failed"), ("A.Two", "Failed")));
        _parameters["variable"] = "ANALYZER_TEST_FAILED_TESTS";
        _parameters["sources"] = "coverage/analyzers/analyzers.trx coverage/absent.trx";
        _parameters["statuses"] = "1";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("A.One, A.Two", variables["ANALYZER_TEST_FAILED_TESTS"]);
        Assert.Contains(_harness.Log, entry => entry.Line == "Failed tests (2): A.One, A.Two");
    }

    [Fact]
    public async Task Publish_NamesTheFlakyListOfASuiteRerunAsFlaky()
    {
        _harness.Write("ws/.qa-test-results/current/flaky-tests.txt", "Integration.Api.Login\n");
        _parameters["variable"] = "QA_CURRENT_INTEGRATION_FLAKY_TESTS";
        _parameters["sources"] = ".qa-test-results/current/flaky-tests.txt";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Integration.Api.Login", variables["QA_CURRENT_INTEGRATION_FLAKY_TESTS"]);
        Assert.Contains(_harness.Log, entry => entry.Line == "Flaky tests (1): Integration.Api.Login");
        Assert.DoesNotContain(_harness.Log, entry => entry.Line.StartsWith("Failed tests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publish_PublishesNothingWithoutAFlakyList()
    {
        _parameters["variable"] = "QA_CURRENT_E2E_FLAKY_TESTS";
        _parameters["sources"] = ".qa-test-results/qa-1-current/e2e/flaky-tests.txt";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(variables);
    }

    [Fact]
    public async Task Publish_SaysNothingWhenNoTestFailed()
    {
        _harness.Write("ws/integration.trx", Trx(("A.One", "Passed")));
        _parameters["variable"] = "QA_CURRENT_INTEGRATION_FAILED_TESTS";
        _parameters["sources"] = "integration.trx";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(variables);
        Assert.DoesNotContain(_harness.Log, entry => entry.Line.StartsWith("Failed tests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publish_SkipsEverythingWhenEveryStatusIsZero()
    {
        _harness.Write("ws/backend.trx", Trx(("A.One", "Failed")));
        _parameters["variable"] = "UNIT_TEST_FAILED_TESTS";
        _parameters["sources"] = "backend.trx";
        _parameters["statuses"] = "0 0 0";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(variables);
    }

    [Theory]
    [InlineData("bad-name")]
    [InlineData("_LEADING")]
    [InlineData("lower")]
    public async Task Publish_RefusesAVariableNameButNeverFailsTheStep(string variable)
    {
        _harness.Write("ws/x.txt", "A\n");
        _parameters["variable"] = variable;
        _parameters["sources"] = "x.txt";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(variables);
        Assert.Contains(_harness.Log, entry =>
            entry.Line.StartsWith($"The failed test names for {variable} could not be published", StringComparison.Ordinal)
            && entry.Level == TaskLogLevel.Warning);
    }

    [Fact]
    public void NamedOutput_IsTheOnlyUndeclaredNameAContextAccepts()
    {
        var context = new PipelineFunctionContext(
            PipelineFunctionCatalog.PublishFailedTests, _harness.Workspace,
            new Dictionary<string, string> { ["variable"] = "UNIT_TEST_FAILED_TESTS", ["sources"] = "x" },
            new Dictionary<string, string>(), (_, _) => Task.CompletedTask);

        context.SetOutput("UNIT_TEST_FAILED_TESTS", "A");

        Assert.Throws<InvalidOperationException>(() => context.SetOutput("OTHER_FAILED_TESTS", "B"));
        Assert.Equal("A", context.Outputs["UNIT_TEST_FAILED_TESTS"]);
    }

    [Fact]
    public void NamedOutput_IsRefusedForAFunctionThatDeclaresNone()
    {
        var context = new PipelineFunctionContext(
            PipelineFunctionCatalog.EnsureNodeRuntime, _harness.Workspace,
            new Dictionary<string, string> { ["variable"] = "UNIT_TEST_FAILED_TESTS" },
            new Dictionary<string, string>(), (_, _) => Task.CompletedTask);

        Assert.Throws<InvalidOperationException>(() => context.SetOutput("UNIT_TEST_FAILED_TESTS", "A"));
    }
}
