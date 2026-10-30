// SPDX-License-Identifier: EUPL-1.2
using System.Security;
using System.Xml.Linq;
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The rerun rule the QA suites share: how a TRX names the test method a failed result is rerun
/// through (a theory case by its method, a name no filter can carry not at all), the filter each runner
/// receives, and the reconciliation of both attempts (failed on both, or flaky in either order).
/// </summary>
public sealed class FailedTestRerunTests
{
    /// <summary>A TRX as Microsoft.Testing.Platform and VSTest write it: namespaced, each result naming
    /// its <c>UnitTest</c> definition, whose <c>TestMethod</c> carries the class and the method, and
    /// counters consistent with the outcomes.</summary>
    internal static string Trx(params (string ClassName, string Method, string Name, string Outcome)[] results)
    {
        var failed = results.Count(result => result.Outcome == "Failed");
        var rows = string.Concat(results.Select((result, index) =>
            $"<UnitTestResult executionId=\"e{index}\" testId=\"t{index}\" testName=\"{SecurityElement.Escape(result.Name)}\" outcome=\"{result.Outcome}\" />"));
        var definitions = string.Concat(results.Select((result, index) =>
            $"<UnitTest name=\"{SecurityElement.Escape(result.Name)}\" id=\"t{index}\"><Execution id=\"e{index}\" />"
            + $"<TestMethod codeBase=\"Tests.dll\" className=\"{SecurityElement.Escape(result.ClassName)}\" name=\"{SecurityElement.Escape(result.Method)}\" /></UnitTest>"));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?><TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">"
               + $"<Results>{rows}</Results><TestDefinitions>{definitions}</TestDefinitions>"
               + $"<ResultSummary outcome=\"{(failed > 0 ? "Failed" : "Completed")}\"><Counters total=\"{results.Length}\" executed=\"{results.Length}\" "
               + $"passed=\"{results.Length - failed}\" failed=\"{failed}\" error=\"0\" timeout=\"0\" aborted=\"0\" disconnected=\"0\" notExecuted=\"0\" />"
               + "</ResultSummary></TestRun>";
    }

    private static List<FailedTestRerun.TestResult> Read(params (string ClassName, string Method, string Name, string Outcome)[] results) =>
        FailedTestRerun.Read(XDocument.Parse(Trx(results)));

    [Fact]
    public void ATheoryCase_IsRerunThroughItsMethodAndKeepsItsOwnName()
    {
        var results = Read(
            ("Suite.Api", "Fact", "Suite.Api.Fact", "Passed"),
            ("Suite.Api", "Theory", "Suite.Api.Theory(method: \"POST\")", "Failed"),
            ("Suite.Ui", "Login(\"blazor\")", "Login(\"blazor\")", "Failed"));

        Assert.Equal(["Suite.Api.Fact", "Suite.Api.Theory", "Suite.Ui.Login"], results.Select(result => result.Method));
        Assert.Equal("Suite.Api.Theory(method: \"POST\")", results[1].Name);
        Assert.Equal([false, true, true], results.Select(result => result.Failed));
        Assert.True(results[0].Passed);
    }

    [Theory]
    [InlineData("", "Method")]
    [InlineData("Suite.Api", "")]
    [InlineData("Suite.Api", "Has Space")]
    [InlineData("Suite|Api", "Method")]
    public void ANameNoFilterCanCarry_NamesNoMethod(string className, string method)
    {
        var result = Assert.Single(Read((className, method, "Suite.Api.Test", "Failed")));

        Assert.Null(result.Method);
    }

    [Fact]
    public void AResultWithoutADefinition_NamesNoMethod()
    {
        var result = Assert.Single(FailedTestRerun.Read(XDocument.Parse(FailedTestNamesTests.Trx(("Suite.Fails", "Failed")))));

        Assert.Null(result.Method);
        Assert.True(result.Failed);
    }

    [Fact]
    public void TheFilters_SelectEachMethodByItsExactName()
    {
        string[] methods = ["Suite.Api.Theory", "Suite.Ui.Login"];

        Assert.Equal(["--filter-method", "Suite.Api.Theory", "Suite.Ui.Login"], FailedTestRerun.TestingPlatformFilter(methods));
        Assert.Equal("FullyQualifiedName=Suite.Api.Theory|FullyQualifiedName=Suite.Ui.Login", FailedTestRerun.VsTestFilter(methods));
    }

    [Fact]
    public void TheVsTestFilter_CarriesNoParenthesis_TheNUnitAdapterRunsOutOfMemoryOnOne()
    {
        // QA #3017: NUnit3TestAdapter 6.3.1 looped in Tokenizer.CollectBalancedParentheticalExpression on
        // "FullyQualifiedName~Method\(" until "Insufficient memory", so the E2E rerun ran nothing.
        Assert.DoesNotContain('(', FailedTestRerun.VsTestFilter(["Aetheus.E2E.AccessibilityTests.Accessibility_KeyPages_HaveNoSeriousOrCriticalViolations"]));
    }

    [Fact]
    public void Reconcile_KeepsTheTestsThatFailedTwiceAndNamesTheFlakyOnesInEitherOrder()
    {
        var first = Read(
            ("S.C", "Stable", "S.C.Stable", "Failed"),
            ("S.C", "Flaky", "S.C.Flaky", "Failed"),
            ("S.C", "Theory", "S.C.Theory(1)", "Failed"),
            ("S.C", "Theory", "S.C.Theory(2)", "Passed"),
            ("S.C", "NotRerun", "S.C.NotRerun", "Failed"));
        var rerun = Read(
            ("S.C", "Stable", "S.C.Stable", "Failed"),
            ("S.C", "Flaky", "S.C.Flaky", "Passed"),
            ("S.C", "Theory", "S.C.Theory(1)", "Passed"),
            ("S.C", "Theory", "S.C.Theory(2)", "Failed"));

        var verdict = FailedTestRerun.Reconcile(first, rerun);

        Assert.Equal(1, verdict.Status);
        Assert.Equal(["S.C.Stable", "S.C.NotRerun"], verdict.Failed);
        Assert.Equal(
            [new FailedTestRerun.FlakyTest("S.C.Flaky", true), new FailedTestRerun.FlakyTest("S.C.Theory(1)", true), new FailedTestRerun.FlakyTest("S.C.Theory(2)", false)],
            verdict.Flaky);
    }

    [Fact]
    public void Reconcile_FailsTheSuiteWhenEveryFailurePassedOnTheRerun_ForAFlakyTestIsADefect()
    {
        var verdict = FailedTestRerun.Reconcile(
            Read(("S.C", "A", "A", "Failed"), ("S.D", "A", "A", "Passed")),
            Read(("S.C", "A", "A", "Passed")));

        Assert.Equal(1, verdict.Status);
        Assert.Empty(verdict.Failed);
        // S.D's A shares the display name, not the class: it is another test, untouched by the rerun.
        Assert.Equal([new FailedTestRerun.FlakyTest("A", true)], verdict.Flaky);
    }
}
