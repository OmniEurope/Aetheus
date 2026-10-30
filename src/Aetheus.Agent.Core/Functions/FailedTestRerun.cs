// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The single rerun the QA integration and E2E suites give their failed tests. Once a first attempt
/// produced gradable evidence with failures, only the failed tests run again, once, through a filter on
/// their test methods. A test that failed on one attempt and passed on the other is flaky, and it still
/// fails its suite (owner decision of 2026-10-27: an unstable test is a defect to fix, never a pass); the
/// rerun only tells it apart. Each one is logged, listed in <c>flaky-tests.txt</c> and in
/// <c>failed-tests.txt</c>, and published by the QA pipeline beside the failed names, which the assurance
/// contract seals. A test
/// that failed on both attempts stays a failure, and so does one the rerun could not run.
///
/// Nothing is rerun when the first attempt is clean, ungradable (the step fails as it always did, a
/// rerun never stands in for missing evidence), or failed more than <see cref="MaxFailures"/> tests.
/// A rerun that produced no gradable evidence leaves the first attempt's verdict in place.
/// </summary>
internal static partial class FailedTestRerun
{
    /// <summary>More failed tests than this are a break, not flakiness: the suite is not rerun and every
    /// failure stands. Ten keeps a few unstable browser or container tests recoverable while a broken
    /// build, which fails dozens, is graded on its first attempt.</summary>
    internal const int MaxFailures = 10;

    internal const string FailedList = "failed-tests.txt";
    internal const string FlakyList = "flaky-tests.txt";

    private static readonly HashSet<string> FailedOutcomes = new(StringComparer.Ordinal) { "Failed", "Error", "Timeout", "Aborted" };

    /// <summary>A method name a filter can carry verbatim: letters, digits, <c>_</c>, the <c>.</c> of the
    /// namespace, the <c>+</c> of a nested class and the backtick of a generic arity. Anything else (a
    /// space, a quote, a parenthesis) is not filtered at all, so its test is not rerun and stays a
    /// failure, rather than a broader pattern rerunning tests nobody asked for.</summary>
    [GeneratedRegex("^[\\p{L}\\p{N}_][\\p{L}\\p{N}_.+`]*\\z")]
    private static partial Regex FilterableMethod();

    /// <summary>One test result of a TRX. <paramref name="Key"/> is its class and display name, which
    /// tell apart two NUnit tests whose display name is only the method name; <paramref name="Method"/>
    /// is the fully qualified test method the rerun filters on, or null when the TRX names none that a
    /// filter can carry. Every case of a theory or parameterized test shares its method, so the rerun of
    /// one failed case reruns its whole method.</summary>
    internal sealed record TestResult(string Key, string Name, string? Method, bool Failed, bool Passed);

    /// <summary>The first attempt's results and the test methods its failures are rerun through.</summary>
    internal sealed record Plan(IReadOnlyList<TestResult> First, IReadOnlyList<string> Methods);

    /// <summary>A test that failed on one attempt and passed on the other; <paramref name="FailedFirst"/>
    /// tells which.</summary>
    internal sealed record FlakyTest(string Name, bool FailedFirst);

    /// <summary>The suite's verdict over both attempts: status 1 while a test failed on both or is flaky.</summary>
    internal sealed record Verdict(int Status, IReadOnlyList<string> Failed, IReadOnlyList<FlakyTest> Flaky);

    /// <summary>The rerun of a first attempt that recorded failures, or null, said in the log, when
    /// nothing is rerun.</summary>
    public static async Task<Plan?> PlanAsync(PipelineFunctionContext context, string suite, string trxPath)
    {
        var first = await ReadAsync(context, suite, trxPath).ConfigureAwait(false);
        if (first is null)
            return null;
        var failed = DistinctByKey(first.Where(result => result.Failed));
        if (failed.Count == 0)
        {
            await context.Log($"{suite}: its TRX names no failed test result, so nothing is rerun.").ConfigureAwait(false);
            return null;
        }
        if (failed.Count > MaxFailures)
        {
            await context.Log(string.Create(CultureInfo.InvariantCulture,
                $"{suite}: {failed.Count} tests failed, more than the rerun cap of {MaxFailures}; nothing is rerun, so many failures are a break rather than flakiness.")).ConfigureAwait(false);
            return null;
        }
        foreach (var unfiltered in failed.Where(result => result.Method is null))
            await context.Log($"{suite}: the failed test {unfiltered.Name} names no test method a filter can select; it is not rerun and stays a failure.",
                TaskLogLevel.Warning).ConfigureAwait(false);
        var methods = failed.Select(result => result.Method).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (methods.Count == 0)
            return null;
        await context.Log(string.Create(CultureInfo.InvariantCulture,
            $"{suite}: rerunning its {failed.Count} failed test(s) once, through {methods.Count} test method(s): {string.Join(", ", methods)}.")).ConfigureAwait(false);
        return new Plan(first, methods);
    }

    /// <summary>The <c>--filter-method</c> arguments of an xunit v3 suite on Microsoft.Testing.Platform:
    /// one option, every method a value, any of which selects a test.</summary>
    public static IEnumerable<string> TestingPlatformFilter(IReadOnlyList<string> methods) => ["--filter-method", .. methods];

    /// <summary>The VSTest <c>--filter</c> expression: each method by its fully qualified name, which
    /// selects every case of a parameterized NUnit test too (NUnit3TestAdapter 6.3.1 matches
    /// <c>Method(args)</c> on <c>FullyQualifiedName=Method</c>). The methods carry no filter operator
    /// (<see cref="FilterableMethod"/>), and the filter carries no parenthesis: the NUnit adapter's own
    /// filter tokenizer runs out of memory on an unbalanced one, escaped or not (QA #3017).</summary>
    public static string VsTestFilter(IReadOnlyList<string> methods) =>
        string.Join('|', methods.Select(method => $"FullyQualifiedName={method}"));

    /// <summary>Both attempts reconciled, or null, said in the log, when the rerun TRX cannot be read
    /// (the first attempt's verdict then stands).</summary>
    public static async Task<Verdict?> ReconcileAsync(PipelineFunctionContext context, string suite, Plan plan, string rerunTrxPath)
    {
        var rerun = await ReadAsync(context, suite, rerunTrxPath).ConfigureAwait(false);
        return rerun is null ? null : Reconcile(plan.First, rerun);
    }

    /// <summary>A test failed when it failed on the first attempt and did not pass on the rerun (a failure
    /// the rerun did not reach included). A flaky test fails the suite too. A test is flaky when it failed on one attempt
    /// and passed on the other, in either order: a theory case that passed first can fail when its
    /// method is rerun.</summary>
    internal static Verdict Reconcile(IReadOnlyList<TestResult> first, IReadOnlyList<TestResult> rerun)
    {
        var passedOnRerun = PassedOnly(rerun);
        var passedFirst = PassedOnly(first);
        var firstFailures = DistinctByKey(first.Where(result => result.Failed));
        var failed = Names(firstFailures.Where(result => !passedOnRerun.Contains(result.Key)));
        var flaky = firstFailures.Where(result => passedOnRerun.Contains(result.Key)).Select(result => new FlakyTest(result.Name, FailedFirst: true))
            .Concat(DistinctByKey(rerun.Where(result => result.Failed && passedFirst.Contains(result.Key))).Select(result => new FlakyTest(result.Name, FailedFirst: false)))
            .DistinctBy(test => test.Name, StringComparer.Ordinal)
            .ToList();
        return new Verdict(failed.Count > 0 || flaky.Count > 0 ? 1 : 0, failed, flaky);
    }

    /// <summary>Each flaky test logged on its own line and listed in <c>flaky-tests.txt</c>, and the tests
    /// that failed on both attempts, then the flaky ones, in <c>failed-tests.txt</c>, one per line, with the
    /// <c>Failed tests (N)</c> line of a suite without rerun: a flaky test fails its suite.</summary>
    public static async Task RecordAsync(PipelineFunctionContext context, string suite, string results, Verdict verdict, CancellationToken cancellationToken)
    {
        foreach (var test in verdict.Flaky)
            await context.Log(test.FailedFirst
                ? $"Flaky test in {suite}: {test.Name} failed, then passed on its rerun."
                : $"Flaky test in {suite}: {test.Name} passed, then failed when its method was rerun.", TaskLogLevel.Warning).ConfigureAwait(false);
        var flakyNames = verdict.Flaky.Select(test => test.Name).ToList();
        if (flakyNames.Count > 0)
        {
            await File.WriteAllTextAsync(Path.Combine(results, FlakyList), Lines(flakyNames), cancellationToken).ConfigureAwait(false);
            await context.Log(FailedTestNames.LogLine(flakyNames, "Flaky tests")).ConfigureAwait(false);
        }
        await context.Log(string.Create(CultureInfo.InvariantCulture,
            $"{suite}: after the rerun, {verdict.Failed.Count} test(s) failed on both attempts and {flakyNames.Count} test(s) are flaky.")).ConfigureAwait(false);
        if (verdict.Status == 0)
            return;
        var failing = verdict.Failed.Concat(flakyNames).Distinct(StringComparer.Ordinal).ToList();
        await File.WriteAllTextAsync(Path.Combine(results, FailedList), Lines(failing), cancellationToken).ConfigureAwait(false);
        await context.Log(FailedTestNames.LogLine(failing)).ConfigureAwait(false);
    }

    /// <summary>Every test result of a TRX with the test method its definition names, or null, said in
    /// the log, when the file cannot be read or is not XML.</summary>
    private static async Task<List<TestResult>?> ReadAsync(PipelineFunctionContext context, string suite, string trxPath)
    {
        try
        {
            return Read(XDocument.Load(trxPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            await context.Log($"{suite}: the TRX {trxPath} cannot be read for a rerun ({ex.Message}); the first attempt's failures stand.",
                TaskLogLevel.Warning).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>The results of a TRX, the namespaced one Microsoft.Testing.Platform and VSTest write or
    /// one without a namespace. A result's test method comes from the <c>UnitTest</c> definition its
    /// <c>testId</c> names: <c>TestMethod className</c> and <c>name</c>, the method name, or the display
    /// name of a parameterized case, whose arguments are dropped.</summary>
    internal static List<TestResult> Read(XDocument trx)
    {
        var methods = new Dictionary<string, (string ClassName, string? Method)>(StringComparer.Ordinal);
        foreach (var unitTest in trx.Descendants().Where(element => element.Name.LocalName == "UnitTest"))
        {
            if (unitTest.Attribute("id")?.Value is not { Length: > 0 } id
                || unitTest.Elements().FirstOrDefault(element => element.Name.LocalName == "TestMethod") is not { } testMethod)
                continue;
            var className = testMethod.Attribute("className")?.Value ?? string.Empty;
            methods[id] = (className, MethodOf(className, testMethod.Attribute("name")?.Value ?? string.Empty));
        }
        var results = new List<TestResult>();
        foreach (var result in trx.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
        {
            if (result.Attribute("testName")?.Value is not { Length: > 0 } testName)
                continue;
            var outcome = result.Attribute("outcome")?.Value ?? string.Empty;
            var known = methods.TryGetValue(result.Attribute("testId")?.Value ?? string.Empty, out var definition);
            results.Add(new TestResult(
                (known ? definition.ClassName : string.Empty) + "\n" + testName,
                FailedTestNames.Sanitize(testName),
                known ? definition.Method : null,
                FailedOutcomes.Contains(outcome),
                outcome == "Passed"));
        }
        return results;
    }

    private static string? MethodOf(string className, string name)
    {
        var parenthesis = name.IndexOf('(', StringComparison.Ordinal);
        var method = parenthesis < 0 ? name : name[..parenthesis];
        if (method.StartsWith(className + ".", StringComparison.Ordinal))
            method = method[(className.Length + 1)..];
        var qualified = className + "." + method;
        return className.Length > 0 && method.Length > 0 && FilterableMethod().IsMatch(qualified) ? qualified : null;
    }

    /// <summary>The keys of the tests that passed and never failed: a key seen failing on an attempt did
    /// not pass it.</summary>
    private static HashSet<string> PassedOnly(IReadOnlyList<TestResult> results)
    {
        var failed = results.Where(result => result.Failed).Select(result => result.Key).ToHashSet(StringComparer.Ordinal);
        return results.Where(result => result.Passed && !failed.Contains(result.Key)).Select(result => result.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static List<TestResult> DistinctByKey(IEnumerable<TestResult> results) =>
        results.DistinctBy(result => result.Key, StringComparer.Ordinal).ToList();

    private static List<string> Names(IEnumerable<TestResult> results) =>
        results.Select(result => result.Name).Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    private static string Lines(IEnumerable<string> names) => string.Concat(names.Select(name => name + "\n"));
}
