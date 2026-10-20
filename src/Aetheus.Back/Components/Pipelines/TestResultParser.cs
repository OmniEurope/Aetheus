// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Xml.Linq;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging;

namespace Aetheus.Back.Components.Pipelines;

public static class TestResultParser
{
    public static List<TestResult> Parse(string xmlContent, string format, int runId, string? stageName, string? stepName, ILogger? logger = null)
    {
        var results = new List<TestResult>();

        try
        {
            var doc = SafeXml.Load(xmlContent);

            if (format.Equals("junit", StringComparison.OrdinalIgnoreCase))
                ParseJUnit(doc, results, runId, stageName, stepName);
            else if (format.Equals("xunit", StringComparison.OrdinalIgnoreCase))
                ParseXUnit(doc, results, runId, stageName, stepName);
            else if (format.Equals("trx", StringComparison.OrdinalIgnoreCase))
                ParseTrx(doc, results, runId, stageName, stepName);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException or ArgumentException)
        {
            // F-04/F-48: malformed or hostile XML (XXE attempts trip DtdProcessing.Prohibit) - skip, return empty.
            logger?.LogWarning("TestResultParser: malformed XML for format {Format} (run {RunId}): {Message}", format, runId, ex.Message);
        }

        return results;
    }

    private static void ParseJUnit(XDocument doc, List<TestResult> results, int runId, string? stageName, string? stepName)
    {
        var testCases = doc.Descendants("testcase");
        foreach (var tc in testCases)
        {
            var outcome = TestOutcome.Passed;
            string? errorMessage = null;
            string? stackTrace = null;

            var failure = tc.Element("failure");
            if (failure is not null)
            {
                outcome = TestOutcome.Failed;
                errorMessage = failure.Attribute("message")?.Value;
                stackTrace = failure.Value;
            }
            else if (tc.Element("error") is not null)
            {
                outcome = TestOutcome.Error;
                errorMessage = tc.Element("error")?.Attribute("message")?.Value;
                stackTrace = tc.Element("error")?.Value;
            }
            else if (tc.Element("skipped") is not null)
            {
                outcome = TestOutcome.Skipped;
            }

            var timeStr = tc.Attribute("time")?.Value;
            _ = double.TryParse(timeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var durationSec);

            results.Add(new TestResult
            {
                PipelineRunId = runId,
                StageName = stageName,
                StepName = stepName,
                TestName = tc.Attribute("name")?.Value ?? "Unknown",
                TestSuite = tc.Attribute("classname")?.Value ?? tc.Parent?.Attribute("name")?.Value,
                Outcome = outcome,
                DurationMs = durationSec * 1000,
                ErrorMessage = errorMessage,
                StackTrace = stackTrace
            });
        }
    }

    private static void ParseXUnit(XDocument doc, List<TestResult> results, int runId, string? stageName, string? stepName)
    {
        var tests = doc.Descendants("test");
        foreach (var test in tests)
        {
            var result = test.Attribute("result")?.Value?.ToLowerInvariant();
            var outcome = result switch
            {
                "pass" => TestOutcome.Passed,
                "fail" => TestOutcome.Failed,
                "skip" => TestOutcome.Skipped,
                _ => TestOutcome.Error
            };

            var timeStr = test.Attribute("time")?.Value;
            _ = double.TryParse(timeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var durationSec);

            var failureEl = test.Element("failure");

            results.Add(new TestResult
            {
                PipelineRunId = runId,
                StageName = stageName,
                StepName = stepName,
                TestName = test.Attribute("name")?.Value ?? "Unknown",
                TestSuite = test.Attribute("type")?.Value,
                Outcome = outcome,
                DurationMs = durationSec * 1000,
                ErrorMessage = failureEl?.Element("message")?.Value,
                StackTrace = failureEl?.Element("stack-trace")?.Value
            });
        }
    }

    private static void ParseTrx(XDocument doc, List<TestResult> results, int runId, string? stageName, string? stepName)
    {
        var suitesByTestId = TrxSuitesByTestId(doc);
        foreach (var test in doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
            results.Add(ToTrxResult(test, suitesByTestId, runId, stageName, stepName));
    }

    /// <summary>The test class of every UnitTest definition, by test id. TRX uses a default namespace
    /// whose value differs between Visual Studio generations: elements are matched by LocalName so both
    /// current and historical result files are accepted.</summary>
    private static Dictionary<string, string?> TrxSuitesByTestId(XDocument doc) =>
        doc.Descendants()
            .Where(e => e.Name.LocalName == "UnitTest")
            .Select(e => new
            {
                Id = e.Attribute("id")?.Value,
                Suite = e.Descendants().FirstOrDefault(d => d.Name.LocalName == "TestMethod")
                    ?.Attribute("className")?.Value
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .ToDictionary(x => x.Id!, x => x.Suite, StringComparer.OrdinalIgnoreCase);

    private static TestResult ToTrxResult(
        XElement test, Dictionary<string, string?> suitesByTestId, int runId, string? stageName, string? stepName)
    {
        var errorInfo = test.Descendants().FirstOrDefault(e => e.Name.LocalName == "ErrorInfo");
        var testId = test.Attribute("testId")?.Value;
        suitesByTestId.TryGetValue(testId ?? string.Empty, out var suite);

        return new TestResult
        {
            PipelineRunId = runId,
            StageName = stageName,
            StepName = stepName,
            TestName = test.Attribute("testName")?.Value ?? "Unknown",
            TestSuite = suite,
            Outcome = TrxOutcome(test.Attribute("outcome")?.Value),
            DurationMs = TrxDurationMs(test.Attribute("duration")?.Value),
            ErrorMessage = TrxErrorChild(errorInfo, "Message"),
            StackTrace = TrxErrorChild(errorInfo, "StackTrace")
        };
    }

    private static TestOutcome TrxOutcome(string? rawOutcome) => rawOutcome?.ToLowerInvariant() switch
    {
        "passed" => TestOutcome.Passed,
        "failed" => TestOutcome.Failed,
        "notexecuted" or "notrunnable" => TestOutcome.Skipped,
        _ => TestOutcome.Error
    };

    private static double TrxDurationMs(string? duration) =>
        TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out var parsed) ? parsed.TotalMilliseconds : 0;

    private static string? TrxErrorChild(XElement? errorInfo, string localName) =>
        errorInfo?.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;
}
