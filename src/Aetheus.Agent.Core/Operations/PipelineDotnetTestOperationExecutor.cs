// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Reconcile runner exit status with TRX evidence to distinguish clean tests, valid
/// findings and broken execution. Publish clean/findings gate values only from trustworthy
/// evidence; otherwise fail the step.</summary>
public sealed class PipelineDotnetTestOperationExecutor(
    ILogger<PipelineDotnetTestOperationExecutor> logger,
    IFileSystemReader files) : EnvironmentOperationExecutor
{
    public override bool CanHandle(OperationKind kind) => kind == OperationKind.PipelineDotnetTest;

    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envVars);
        ArgumentNullException.ThrowIfNull(onOutput);

        if (kind != OperationKind.PipelineDotnetTest) return new ExecutorResult(-1, false);

        if (PipelineDotnetTestRequest.TryCreate(target, envVars, out var request, out var rejection) is false)
        {
            await onOutput(rejection!, TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false, "invalid-request", rejection);
        }

        var testingPlatform = DotnetTestCommandLine.UsesTestingPlatform(request!.Workspace);
        await onOutput(
            testingPlatform
                ? "global.json selects Microsoft.Testing.Platform; running the suite with its command line."
                : "No global.json test runner selected; running the suite with the VSTest command line.",
            TaskLogLevel.Info).ConfigureAwait(false);
        var startInfo = DotnetTestCommandLine.Build(request, testingPlatform);
        // Evidence left by an earlier run must never grade this one: the TRX this step names is
        // removed, and the coverage reports already present are remembered so that only a report
        // this run wrote counts.
        if (File.Exists(request.TrxPath)) File.Delete(request.TrxPath);
        var staleCoverage = request.CollectCoverage ? SnapshotCoverageReports(request.ResultsDirectory) : null;
        // The suite's own exit code is captured rather than allowed to end the step: a test that ran
        // and failed is graded, and only unusable evidence is a technical failure of the run. On the
        // testing platform a failed test exits 2, not 1; the classifier reads the TRX, not the value.
        var project = request.Project;
        var run = await ProgressHeartbeat.RunAsync(
            () => ProcessRunner.RunAsync(startInfo, timeoutSeconds, onOutput, logger, cancellationToken),
            request.ProgressInterval,
            elapsed => onOutput(
                $"{project}: still running after {(long)elapsed.TotalSeconds}s.", TaskLogLevel.Info),
            cancellationToken).ConfigureAwait(false);
        if (run.TimedOut)
        {
            await onOutput(
                "The test run exceeded its timeout, so it produced no verdict to classify.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return run;
        }
        return await GradeAsync(request, testingPlatform, staleCoverage, run.ExitCode, onOutput).ConfigureAwait(false);
    }

    /// <summary>Grades a finished run from its evidence: the coverage reports it wrote, then the TRX
    /// read with its exit code. Publishes the findings status, or fails the step when the evidence
    /// cannot be trusted.</summary>
    private async Task<ExecutorResult> GradeAsync(
        PipelineDotnetTestRequest request, bool testingPlatform, HashSet<(long Length, DateTime WrittenUtc)>? staleCoverage,
        int exitCode, Func<string, TaskLogLevel, Task> onOutput)
    {
        if (testingPlatform && request.CollectCoverage)
            foreach (var report in DotnetTestCommandLine.NormalizeCoverageReports(request.ResultsDirectory))
                await onOutput($"Coverage report: {report}", TaskLogLevel.Info).ConfigureAwait(false);
        if (staleCoverage is { Count: > 0 } && !HasFreshCoverageReport(request.ResultsDirectory, staleCoverage))
        {
            const string reason = "Every Cobertura report in the results directory predates this run; it wrote none of its own.";
            await onOutput($"The test evidence cannot be graded: {reason}", TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false, "untrustworthy-evidence", reason);
        }

        var verdict = DotnetTestResultClassifier.Classify(
            NormalizeExitCode(exitCode),
            request.TrxPath,
            request.CollectCoverage ? request.ResultsDirectory : null,
            files);

        if (!verdict.IsValue)
        {
            // Not a findings status: the run cannot be graded at all, and reporting it as "1 finding"
            // would let a broken test host pass for an honest regression.
            await onOutput(
                $"The test evidence cannot be graded: {verdict.Failure.Reason}",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false, "untrustworthy-evidence", verdict.Failure.Reason);
        }

        var status = verdict.Value.ToString(CultureInfo.InvariantCulture);
        await onOutput(
            $"##aetheus[setvariable name={request.StatusVariable}]{status}", TaskLogLevel.Info)
            .ConfigureAwait(false);
        await onOutput(
            $"{request.Project} findings status: {status}. Valid TRX"
            + (request.CollectCoverage ? " and coverage evidence" : " evidence") + " recorded.",
            TaskLogLevel.Info).ConfigureAwait(false);

        // The step succeeds either way: findings travel as the published status, not as a red step,
        // which is what lets the gate decide what a finding costs.
        return new ExecutorResult(0, false);
    }

    /// <summary>
    /// A process can die on a signal and report a negative or very large code. The classifier only
    /// accepts 0..255, and every such code means the same thing here: not zero.
    /// </summary>
    private static int NormalizeExitCode(int exitCode) => exitCode is >= 0 and <= 255 ? exitCode : 1;

    // A report is identified by its size and write time, which a move (the stamped-report
    // normalisation) preserves: a stale report renamed after the run is still recognised as stale.
    private static HashSet<(long Length, DateTime WrittenUtc)> SnapshotCoverageReports(string resultsDirectory) =>
        Directory.Exists(resultsDirectory)
            ? Directory.EnumerateFiles(resultsDirectory, "coverage.cobertura*.xml", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Select(info => (info.Length, info.LastWriteTimeUtc))
                .ToHashSet()
            : [];

    private static bool HasFreshCoverageReport(
        string resultsDirectory, HashSet<(long Length, DateTime WrittenUtc)> stale) =>
        SnapshotCoverageReports(resultsDirectory).Any(report => !stale.Contains(report));
}
