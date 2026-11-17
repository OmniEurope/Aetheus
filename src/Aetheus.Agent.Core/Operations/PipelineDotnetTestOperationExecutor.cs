// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Reconcile runner exit status with TRX evidence to distinguish clean tests, valid
/// findings and broken execution. Publish clean/findings gate values only from trustworthy
/// evidence; otherwise fail the step. With <c>rerun_failed_tests</c>, the failed tests of a
/// gradable first attempt run once more (<see cref="FailedTestRerun"/>): only the tests that failed
/// on both attempts then count in the status, the flaky ones are named beside it.</summary>
public sealed class PipelineDotnetTestOperationExecutor(
    ILogger<PipelineDotnetTestOperationExecutor> logger,
    IFileSystemReader files) : EnvironmentOperationExecutor
{
    /// <summary>The rerun's TRX lands here, under the results directory, so the first attempt's
    /// evidence is never overwritten.</summary>
    internal const string RerunDirectory = "rerun";

    /// <summary>A rerun is not started with less time than this left of the step's timeout: the
    /// first attempt's failures then stand.</summary>
    private const int MinimumRerunSeconds = 30;

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
        var clock = Stopwatch.StartNew();
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
        var (status, failure) = await GradeAsync(request, testingPlatform, staleCoverage, run.ExitCode, onOutput).ConfigureAwait(false);
        if (failure is not null)
            return failure;
        if (status != 0 && request.RerunFailedTests)
            status = await RerunAsync(request, testingPlatform, timeoutSeconds - (int)clock.Elapsed.TotalSeconds, onOutput, cancellationToken)
                .ConfigureAwait(false);
        return await PublishAsync(request, status, onOutput).ConfigureAwait(false);
    }

    /// <summary>The failed tests of a first attempt that recorded findings, run once more into
    /// <see cref="RerunDirectory"/> without coverage, and the status over both attempts: 0 when every
    /// failure passed on the rerun (flaky tests, named in <c>flaky-tests.txt</c>), 1 otherwise. Whatever
    /// keeps the rerun from grading (nothing to rerun, too many failures, too little time left, a timeout,
    /// untrustworthy rerun evidence) leaves the first attempt's 1 in place.</summary>
    private async Task<int> RerunAsync(
        PipelineDotnetTestRequest request, bool testingPlatform, int remainingSeconds,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        FailedTestRerun.StepLog log = (message, level) => onOutput(message, level);
        if (await FailedTestRerun.PlanAsync(log, request.Project, request.TrxPath).ConfigureAwait(false) is not { } plan)
            return 1;
        if (remainingSeconds < MinimumRerunSeconds)
        {
            await log(string.Create(CultureInfo.InvariantCulture,
                $"{request.Project}: only {Math.Max(remainingSeconds, 0)}s of the step's timeout are left, so its failed tests are not rerun and stand."),
                TaskLogLevel.Warning).ConfigureAwait(false);
            return 1;
        }
        var rerun = request with
        {
            ResultsDirectory = Path.Combine(request.ResultsDirectory, RerunDirectory),
            CollectCoverage = false,
            RerunFailedTests = false,
            RerunMethods = plan.Methods
        };
        if (File.Exists(rerun.TrxPath)) File.Delete(rerun.TrxPath);
        var result = await ProcessRunner.RunAsync(
            DotnetTestCommandLine.Build(rerun, testingPlatform), remainingSeconds, onOutput, logger, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            await log($"{request.Project}: the rerun exceeded the step's timeout; the first attempt's failures stand.", TaskLogLevel.Warning).ConfigureAwait(false);
            return 1;
        }
        var verdict = DotnetTestResultClassifier.Classify(NormalizeExitCode(result.ExitCode), rerun.TrxPath, null, files);
        if (!verdict.IsValue)
        {
            await log($"{request.Project}: the rerun evidence cannot be graded ({verdict.Failure.Reason}); the first attempt's failures stand.",
                TaskLogLevel.Warning).ConfigureAwait(false);
            return 1;
        }
        if (await FailedTestRerun.ReconcileAsync(log, request.Project, plan, rerun.TrxPath).ConfigureAwait(false) is not { } reconciled)
            return 1;
        await FailedTestRerun.RecordAsync(log, request.Project, request.ResultsDirectory, reconciled, cancellationToken).ConfigureAwait(false);
        return reconciled.Status;
    }

    /// <summary>Publishes the findings status of trustworthy evidence on a green step.</summary>
    private static async Task<ExecutorResult> PublishAsync(
        PipelineDotnetTestRequest request, int value, Func<string, TaskLogLevel, Task> onOutput)
    {
        var status = value.ToString(CultureInfo.InvariantCulture);
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

    /// <summary>Grades a finished run from its evidence: the coverage reports it wrote, then the TRX
    /// read with its exit code. Returns the findings status, or the failed step when the evidence
    /// cannot be trusted.</summary>
    private async Task<(int Status, ExecutorResult? Failure)> GradeAsync(
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
            return (1, new ExecutorResult(1, false, "untrustworthy-evidence", reason));
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
            return (1, new ExecutorResult(1, false, "untrustworthy-evidence", verdict.Failure.Reason));
        }

        return (verdict.Value, null);
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
