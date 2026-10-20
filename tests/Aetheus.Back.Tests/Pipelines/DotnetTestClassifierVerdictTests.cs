// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The verdict of the test-result classifier on a fixed corpus: whether a suite ran clean,
/// ran and failed, or produced no evidence that can be graded, and for a refusal which rule refused.
/// Each expected verdict is the one <c>deploy/scripts/classify-dotnet-test-result.mjs</c> gave for the
/// same case on 2026-10-16, when the C# classifier was still checked against it; the script was then
/// deleted (PLAN-002 lot 1.10, B2) and this corpus keeps the two from having diverged.</summary>
public sealed class DotnetTestClassifierVerdictTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "aetheus-classifier-verdict-" + Guid.NewGuid().ToString("N"));

    /// <summary>Every case is (exit code, TRX, coverage reports, expected status or null, expected
    /// refusal marker or null). Named so a failure says which situation changed verdict.</summary>
    public static TheoryData<string, int, string, string[], int?, string?> Cases()
    {
        const string validCoverage =
            """<coverage lines-valid="10" lines-covered="8" line-rate="0.8" />""";

        return new TheoryData<string, int, string, string[], int?, string?>
        {
            { "clean run", 0, Trx(), [validCoverage], 0, null },
            { "failed assertions", 1, Trx(failed: 2, outcome: "Failed"), [validCoverage], 1, null },
            { "test host error", 1, Trx(error: 1, outcome: "Failed"), [validCoverage], null, "technical test-host failure" },
            // Reaches the technical-failure check rather than stopping at the exit-code
            // reconciliation before it: the counters here are otherwise a perfectly gradeable
            // "ran and failed", so only the error counter can refuse it.
            {
                "test host error alongside real failures", 1, Trx(failed: 1, error: 1, outcome: "Failed"), [validCoverage],
                null, "technical test-host failure"
            },
            {
                "zero exit contradicted by failures", 0, Trx(failed: 1, outcome: "Failed"), [validCoverage],
                null, "exit code and TRX counters are inconsistent"
            },
            {
                "non-zero exit with nothing failed", 1, Trx(), [validCoverage],
                null, "exit code and TRX counters are inconsistent"
            },
            { "aborted outcome", 1, Trx(outcome: "Aborted"), [validCoverage], null, "is incomplete" },
            { "executed exceeds total", 0, Trx(executedDelta: 1), [validCoverage], null, "execution counters are inconsistent" },
            { "missing failed counter", 0, TrxWithoutFailedCounter(), [validCoverage], null, "missing or invalid" },
            { "no coverage report", 0, Trx(), [], null, "at least one Cobertura report" },
            { "second coverage report malformed", 0, Trx(), [validCoverage, "<not-coverage />"], null, "no coverage root" },
            {
                "impossible coverage counters", 0, Trx(),
                ["""<coverage lines-valid="4" lines-covered="9" line-rate="0.75" />"""],
                null, "line counters are missing or invalid"
            },
            { "empty trx", 0, string.Empty, [validCoverage], null, "TRX evidence" },
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task TheClassifierReachesTheExpectedVerdict(
        string caseName, int exitCode, string trx, string[] coverageReports, int? expectedStatus, string? expectedRefusal)
    {
        var directory = Path.Combine(_root, SafeName(caseName));
        var trxPath = Path.Combine(directory, "result.trx");
        var coverageDirectory = Path.Combine(directory, "coverage");
        Directory.CreateDirectory(coverageDirectory);
        await File.WriteAllTextAsync(trxPath, trx, TestContext.Current.CancellationToken);
        for (var index = 0; index < coverageReports.Length; index++)
        {
            // Nested one level down for every report after the first, which is how a multi-assembly
            // run actually lays them out.
            var reportDirectory = index == 0
                ? coverageDirectory
                : Path.Combine(coverageDirectory, "assembly" + index.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(reportDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(reportDirectory, "coverage.cobertura.xml"),
                coverageReports[index],
                TestContext.Current.CancellationToken);
        }

        var verdict = DotnetTestResultClassifier.Classify(
            exitCode, trxPath, coverageDirectory, new PhysicalFileSystemReader());

        if (expectedStatus is { } status)
        {
            Assert.True(verdict.IsValue,
                $"'{caseName}': expected status {status}, the classifier refused: "
                + (verdict.IsValue ? string.Empty : verdict.Failure.Reason));
            Assert.Equal(status, verdict.Value);
            return;
        }

        // Refusing is not enough: refusing for another rule means the classifier now applies a rule
        // it did not, and the next case is where that shows up as a broken run graded clean.
        Assert.False(verdict.IsValue, $"'{caseName}': expected a refusal, the classifier graded it.");
        Assert.Contains(expectedRefusal!, verdict.Failure.Reason, StringComparison.Ordinal);
    }

    private static string Trx(
        int failed = 0, int error = 0, string outcome = "Completed", int executedDelta = 0)
    {
        var passed = failed + error > 0 ? 1 : 2;
        var total = passed + failed + error;
        return $"""<TestRun><ResultSummary outcome="{outcome}"><Counters total="{total}" executed="{total + executedDelta}" passed="{passed}" failed="{failed}" error="{error}" timeout="0" aborted="0" disconnected="0" /></ResultSummary></TestRun>""";
    }

    private static string TrxWithoutFailedCounter() =>
        """<TestRun><ResultSummary outcome="Completed"><Counters total="2" executed="2" passed="2" error="0" timeout="0" aborted="0" disconnected="0" /></ResultSummary></TestRun>""";

    private static string SafeName(string caseName) =>
        string.Concat(caseName.Select(character => char.IsLetterOrDigit(character) ? character : '-'));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
