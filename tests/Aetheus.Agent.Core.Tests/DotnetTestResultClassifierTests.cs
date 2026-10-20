// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The one decision this classifier exists to make: a suite that ran and failed is a finding, a
/// suite whose evidence cannot be trusted is a technical failure of the run. Everything below either
/// pins one side of that line or pins a way the two could be confused.
/// </summary>
public sealed class DotnetTestResultClassifierTests
{
    /// <summary>A TRX shaped like the ones the runner actually writes. `passed` is derived so the
    /// counters stay internally consistent, which is what the classifier checks first.</summary>
    private static string Trx(
        int failed = 0, int error = 0, string outcome = "Completed", int executedDelta = 0)
    {
        var passed = failed + error > 0 ? 1 : 2;
        var total = passed + failed + error;
        return $"""
            <TestRun><ResultSummary outcome="{outcome}"><Counters total="{total}"
            executed="{total + executedDelta}" passed="{passed}" failed="{failed}" error="{error}"
            timeout="0" aborted="0" disconnected="0" /></ResultSummary></TestRun>
            """.ReplaceLineEndings(" ");
    }

    private const string ValidCoverage =
        """<coverage lines-valid="10" lines-covered="8" line-rate="0.8" />""";

    private static FakeFiles Files(string trx, params (string Path, string Content)[] coverage)
    {
        var files = new FakeFiles();
        files.Add("/w/result.trx", trx);
        foreach (var (path, content) in coverage) files.Add(path, content);
        return files;
    }

    private static OneOf<int, DotnetTestClassificationFailure> Classify(
        int exitCode, FakeFiles files, string? coverageDirectory = "/w/coverage") =>
        DotnetTestResultClassifier.Classify(exitCode, "/w/result.trx", coverageDirectory, files);

    // --- The two gradeable outcomes ---

    [Fact]
    public void APassingRunWithCoverageIsGradedZero()
    {
        var result = Classify(0, Files(Trx(), ("/w/coverage/coverage.cobertura.xml", ValidCoverage)));

        Assert.True(result.IsValue);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public void ARunThatFailedAssertionsIsAFindingNotABrokenRun()
    {
        var result = Classify(
            1,
            Files(Trx(failed: 2, outcome: "Failed"), ("/w/coverage/coverage.cobertura.xml", ValidCoverage)));

        Assert.True(result.IsValue);
        Assert.Equal(1, result.Value);
    }

    [Fact]
    public void CoverageIsNotRequiredWhenTheStepDoesNotCollectIt()
    {
        var result = Classify(0, Files(Trx()), coverageDirectory: null);

        Assert.True(result.IsValue);
        Assert.Equal(0, result.Value);
    }

    // --- Evidence that cannot be graded ---

    [Fact]
    public void ATestHostFailureIsRefusedRatherThanCountedAsAFinding()
    {
        // The whole point: `error` means the host broke, so the suite proved nothing, and grading it
        // as "1 finding" would let a crashed run pass for an honest regression.
        var result = Classify(
            1, Files(Trx(error: 1, outcome: "Failed"), ("/w/coverage/coverage.cobertura.xml", ValidCoverage)));

        Assert.False(result.IsValue);
        Assert.Contains("technical test-host failure", result.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASuccessExitCodeContradictedByFailedCountersIsRefused()
    {
        var result = Classify(
            0,
            Files(Trx(failed: 1, outcome: "Failed"), ("/w/coverage/coverage.cobertura.xml", ValidCoverage)));

        Assert.False(result.IsValue);
        Assert.Contains("inconsistent", result.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureExitCodeWithNothingFailedIsRefused()
    {
        var result = Classify(1, Files(Trx(), ("/w/coverage/coverage.cobertura.xml", ValidCoverage)));

        Assert.False(result.IsValue);
        Assert.Contains("inconsistent", result.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncompleteOutcomeIsRefused()
    {
        var result = Classify(
            1, Files(Trx(outcome: "Aborted"), ("/w/coverage/coverage.cobertura.xml", ValidCoverage)));

        Assert.False(result.IsValue);
        Assert.Contains("incomplete", result.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CountersClaimingMoreExecutedThanTotalAreRefused()
    {
        var result = Classify(
            0, Files(Trx(executedDelta: 1), ("/w/coverage/coverage.cobertura.xml", ValidCoverage)));

        Assert.False(result.IsValue);
        Assert.Contains("inconsistent", result.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingCounterIsRefusedInsteadOfDefaultingToZero()
    {
        // A truncated TRX must not read as a clean run.
        var trx = """
            <TestRun><ResultSummary outcome="Completed"><Counters total="2" executed="2" passed="2"
            error="0" timeout="0" aborted="0" disconnected="0" /></ResultSummary></TestRun>
            """.ReplaceLineEndings(" ");

        var result = Classify(0, Files(trx, ("/w/coverage/coverage.cobertura.xml", ValidCoverage)));

        Assert.False(result.IsValue);
        Assert.Contains("missing or invalid", result.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyTrxIsRefused()
    {
        var files = new FakeFiles();
        files.Add("/w/result.trx", string.Empty);

        var result = Classify(0, files, coverageDirectory: null);

        Assert.False(result.IsValue);
        Assert.Contains("TRX evidence", result.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrxLargerThanTheEvidenceCeilingIsRefusedWithoutBeingRead()
    {
        var files = new FakeFiles();
        files.AddOversized("/w/result.trx", DotnetTestResultClassifier.MaxEvidenceBytes + 1);

        var result = Classify(0, files, coverageDirectory: null);

        Assert.False(result.IsValue);
        Assert.False(files.WasRead("/w/result.trx"));
    }

    [Fact]
    public void TwoResultSummariesAreRefused()
    {
        var result = Classify(0, Files(Trx() + Trx()), coverageDirectory: null);

        Assert.False(result.IsValue);
        Assert.Contains("exactly one result summary", result.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncompleteOutcomeIsRefusedBeforeATechnicalFailureIsReported()
    {
        var result = Classify(1, Files(Trx(error: 1, outcome: "Aborted")), coverageDirectory: null);

        Assert.False(result.IsValue);
        Assert.Contains("outcome 'Aborted' is incomplete", result.Failure.Reason, StringComparison.Ordinal);
    }

    // --- Coverage evidence ---

    [Fact]
    public void ACoverageRunThatProducedNoReportIsRefused()
    {
        var result = Classify(0, Files(Trx()));

        Assert.False(result.IsValue);
        Assert.Contains("at least one Cobertura report", result.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryReportOfAMultiAssemblyRunIsValidatedNotJustTheFirst()
    {
        var result = Classify(0, Files(
            Trx(),
            ("/w/coverage/coverage.cobertura.xml", ValidCoverage),
            ("/w/coverage/second/coverage.cobertura.xml", "<not-coverage />")));

        Assert.False(result.IsValue);
        Assert.Contains("no coverage root", result.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ImpossibleCoverageCountersAreRefused()
    {
        var result = Classify(0, Files(
            Trx(),
            ("/w/coverage/coverage.cobertura.xml",
                """<coverage lines-valid="4" lines-covered="9" line-rate="0.75" />""")));

        Assert.False(result.IsValue);
        Assert.Contains("line counters are missing or invalid", result.Failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExitCodeOutsideTheProcessRangeIsRefused()
    {
        var result = Classify(256, Files(Trx()), coverageDirectory: null);

        Assert.False(result.IsValue);
        Assert.Contains("exit code is invalid", result.Failure.Reason, StringComparison.Ordinal);
    }

    /// <summary>An in-memory disk, so every rule above is pinned without touching the file system.</summary>
    private sealed class FakeFiles : IFileSystemReader
    {
        private readonly Dictionary<string, string> _contents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _sizes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _read = new(StringComparer.Ordinal);

        public void Add(string path, string content)
        {
            _contents[path] = content;
            _sizes[path] = content.Length;
        }

        public void AddOversized(string path, long size)
        {
            _contents[path] = string.Empty;
            _sizes[path] = size;
        }

        public bool WasRead(string path) => _read.Contains(path);

        public long FileSize(string path) => _sizes.TryGetValue(path, out var size) ? size : -1;

        public string ReadText(string path)
        {
            _read.Add(path);
            return _contents[path];
        }

        public IReadOnlyList<string> FindFiles(string directory, string fileName)
        {
            var prefix = directory.EndsWith('/') ? directory : directory + "/";
            return [.. _contents.Keys
                .Where(path => path.StartsWith(prefix, StringComparison.Ordinal)
                    && path.EndsWith("/" + fileName, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)];
        }
    }
}
