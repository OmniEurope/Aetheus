// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// End to end for the typed step, against a real process: a tiny script stands in for the test
/// runner, writes the TRX and coverage the case stages for it into the results directory, and exits
/// with the code the case needs. That is what makes these tests prove the step's actual contract
/// rather than the classifier's, which is pinned separately.
///
/// The contract being pinned: findings travel as a PUBLISHED STATUS on a green step, never as a red
/// one. A red step would tell the gate nothing about whether the suite ran, and that distinction is
/// the entire reason the step exists.
/// </summary>
public sealed class PipelineDotnetTestOperationExecutorTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "aetheus-dotnet-test-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> _output = [];

    private const string ValidCoverage =
        """<coverage lines-valid="10" lines-covered="8" line-rate="0.8" />""";

    private static string Trx(int failed = 0, int error = 0, string outcome = "Completed")
    {
        var passed = failed + error > 0 ? 1 : 2;
        var total = passed + failed + error;
        return $"""<TestRun><ResultSummary outcome="{outcome}"><Counters total="{total}" executed="{total}" passed="{passed}" failed="{failed}" error="{error}" timeout="0" aborted="0" disconnected="0" /></ResultSummary></TestRun>""";
    }

    /// <summary>
    /// Stands in for the test runner. Like a real one it writes its evidence during the run: it
    /// copies whatever the case staged into the results directory, then exits with the code the
    /// case is about, which is the input the step reconciles against that evidence.
    /// </summary>
    private string FakeRunner(int exitCode)
    {
        var path = Path.Combine(
            _workspace, OperatingSystem.IsWindows() ? "runner.cmd" : "runner.sh");
        File.WriteAllText(
            path,
            OperatingSystem.IsWindows()
                ? $"@echo off\r\n{WindowsPublishStaged}exit /b {exitCode}\r\n"
                : $"#!/bin/sh\n{PosixPublishStaged}exit {exitCode}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    // Script lines that publish the staged evidence into coverage/backend, next to the script.
    private const string WindowsPublishStaged =
        "if exist \"%~dp0staged\\*\" copy /y \"%~dp0staged\\*\" \"%~dp0coverage\\backend\\\" >nul\r\n";
    private const string PosixPublishStaged =
        "d=\"$(dirname \"$0\")\"; cp \"$d\"/staged/* \"$d/coverage/backend/\" 2>/dev/null\n";

    /// <summary>Evidence the fake runner writes into the results directory when it runs.</summary>
    private void Stage(string fileName, string content)
    {
        var staged = Directory.CreateDirectory(Path.Combine(_workspace, "staged")).FullName;
        File.WriteAllText(Path.Combine(staged, fileName), content);
    }

    private string ResultsDirectory => Directory.CreateDirectory(Path.Combine(_workspace, "coverage", "backend")).FullName;

    private async Task<ExecutorResult> RunAsync(
        int runnerExitCode,
        string? trx,
        string? coverage = null,
        bool collectCoverage = false)
    {
        _ = ResultsDirectory;
        if (trx is not null) Stage("backend.trx", trx);
        if (coverage is not null) Stage("coverage.cobertura.xml", coverage);

        var executor = new PipelineDotnetTestOperationExecutor(
            NullLogger<PipelineDotnetTestOperationExecutor>.Instance, new PhysicalFileSystemReader());

        return await executor.ExecuteAsync(
            OperationKind.PipelineDotnetTest,
            "tests/Sample.Tests",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WORKSPACE"] = _workspace,
                [PipelineDotnetTestVariables.ResultsDirectory] = "coverage/backend",
                [PipelineDotnetTestVariables.StatusVariable] = "BACKEND_STATUS",
                [PipelineDotnetTestVariables.TrxName] = "backend.trx",
                [PipelineDotnetTestVariables.CollectCoverage] = collectCoverage ? "true" : "false",
                [PipelineDotnetTestVariables.DotnetPath] = FakeRunner(runnerExitCode)
            },
            timeoutSeconds: 60,
            onOutput: (line, _) =>
            {
                _output.Add(line);
                return Task.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private string? PublishedStatus() => _output
        .FirstOrDefault(line => line.StartsWith("##aetheus[setvariable name=BACKEND_STATUS]", StringComparison.Ordinal))
        ?.Split(']')[^1];

    public PipelineDotnetTestOperationExecutorTests() => Directory.CreateDirectory(_workspace);

    [Fact]
    public async Task ACleanSuitePublishesAZeroStatusOnAGreenStep()
    {
        var result = await RunAsync(runnerExitCode: 0, trx: Trx());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0", PublishedStatus());
    }

    [Fact]
    public async Task ASuiteWithFailuresPublishesAOneStatusAndStillSucceedsAsAStep()
    {
        // The heart of it: the suite ran, so its findings are the gate's business, not the step's.
        // A red step here would make an honest regression indistinguishable from a broken runner.
        var result = await RunAsync(runnerExitCode: 1, trx: Trx(failed: 2, outcome: "Failed"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1", PublishedStatus());
    }

    [Fact]
    public async Task AMissingTrxFailsTheStepAndPublishesNothing()
    {
        var result = await RunAsync(runnerExitCode: 1, trx: null);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("untrustworthy-evidence", result.FailureCode);
        Assert.Null(PublishedStatus());
    }

    [Fact]
    public async Task ATestHostFailureFailsTheStepRatherThanPublishingAFindingsStatus()
    {
        var result = await RunAsync(
            runnerExitCode: 1, trx: Trx(failed: 1, error: 1, outcome: "Failed"));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("untrustworthy-evidence", result.FailureCode);
        Assert.Contains("technical test-host failure", result.FailureReason!, StringComparison.Ordinal);
        Assert.Null(PublishedStatus());
    }

    [Fact]
    public async Task ACoverageRunThatProducedNoReportFailsTheStep()
    {
        var result = await RunAsync(
            runnerExitCode: 0, trx: Trx(), coverage: null, collectCoverage: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Cobertura", result.FailureReason!, StringComparison.Ordinal);
        Assert.Null(PublishedStatus());
    }

    [Fact]
    public async Task ACoverageRunWithValidEvidencePublishesItsStatus()
    {
        var result = await RunAsync(
            runnerExitCode: 0, trx: Trx(), coverage: ValidCoverage, collectCoverage: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0", PublishedStatus());
    }

    [Fact]
    public async Task ATrxLeftByAnEarlierRunIsNotThisRunsEvidence()
    {
        // A valid report from a previous run, and a runner that exits 0 without writing one.
        await File.WriteAllTextAsync(
            Path.Combine(ResultsDirectory, "backend.trx"), Trx(), TestContext.Current.CancellationToken);

        var result = await RunAsync(runnerExitCode: 0, trx: null);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("untrustworthy-evidence", result.FailureCode);
        Assert.Null(PublishedStatus());
    }

    [Fact]
    public async Task ACoverageReportLeftByAnEarlierRunIsNotThisRunsEvidence()
    {
        var earlier = Directory.CreateDirectory(Path.Combine(ResultsDirectory, "earlier-run")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(earlier, "coverage.cobertura.xml"), ValidCoverage, TestContext.Current.CancellationToken);

        var result = await RunAsync(runnerExitCode: 0, trx: Trx(), coverage: null, collectCoverage: true);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("untrustworthy-evidence", result.FailureCode);
        Assert.Contains("predates this run", result.FailureReason!, StringComparison.Ordinal);
        Assert.Null(PublishedStatus());
    }

    [Fact]
    public async Task AnInvalidStepDefinitionIsRefusedBeforeAnyProcessRuns()
    {
        var executor = new PipelineDotnetTestOperationExecutor(
            NullLogger<PipelineDotnetTestOperationExecutor>.Instance, new PhysicalFileSystemReader());

        var result = await executor.ExecuteAsync(
            OperationKind.PipelineDotnetTest,
            "../escape",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WORKSPACE"] = _workspace,
                [PipelineDotnetTestVariables.ResultsDirectory] = "coverage",
                [PipelineDotnetTestVariables.StatusVariable] = "BACKEND_STATUS"
            },
            timeoutSeconds: 60,
            onOutput: (line, _) =>
            {
                _output.Add(line);
                return Task.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("invalid-request", result.FailureCode);
        Assert.Null(PublishedStatus());
    }

    [Fact]
    public async Task ASilentSuiteWritesProgressLinesWhileItRuns()
    {
        // run-with-progress.sh wrote a line every interval so a long silent suite could be told apart
        // from a stalled runner; the typed step keeps that, at the interval the run variable sets.
        _ = ResultsDirectory;
        Stage("backend.trx", Trx());
        var runner = Path.Combine(_workspace, OperatingSystem.IsWindows() ? "slow.cmd" : "slow.sh");
        File.WriteAllText(
            runner,
            OperatingSystem.IsWindows()
                ? $"@echo off\r\nping -n 4 127.0.0.1 >nul\r\n{WindowsPublishStaged}exit /b 0\r\n"
                : $"#!/bin/sh\nsleep 3\n{PosixPublishStaged}exit 0\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(runner, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var executor = new PipelineDotnetTestOperationExecutor(
            NullLogger<PipelineDotnetTestOperationExecutor>.Instance, new PhysicalFileSystemReader());

        var result = await executor.ExecuteAsync(
            OperationKind.PipelineDotnetTest,
            "tests/Sample.Tests",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WORKSPACE"] = _workspace,
                [PipelineDotnetTestVariables.ResultsDirectory] = "coverage/backend",
                [PipelineDotnetTestVariables.StatusVariable] = "BACKEND_STATUS",
                [PipelineDotnetTestVariables.TrxName] = "backend.trx",
                [PipelineDotnetTestVariables.DotnetPath] = runner,
                [PipelineDotnetTestVariables.ProgressIntervalSeconds] = "1"
            },
            timeoutSeconds: 60,
            onOutput: (line, _) =>
            {
                lock (_output) _output.Add(line);
                return Task.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0", PublishedStatus());
        Assert.Contains(_output, line => line.StartsWith("tests/Sample.Tests: still running after ", StringComparison.Ordinal));
        // The progress lines stop with the suite: none follows the published verdict.
        var verdict = _output.FindIndex(line => line.StartsWith("##aetheus[setvariable name=BACKEND_STATUS]", StringComparison.Ordinal));
        Assert.DoesNotContain(_output.Skip(verdict), line => line.Contains("still running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnpublishedDotnetOutputIsRefusedInsteadOfRunningTheHostSdk()
    {
        var executor = new PipelineDotnetTestOperationExecutor(
            NullLogger<PipelineDotnetTestOperationExecutor>.Instance, new PhysicalFileSystemReader());

        var result = await executor.ExecuteAsync(
            OperationKind.PipelineDotnetTest,
            "tests/Sample.Tests",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WORKSPACE"] = _workspace,
                [PipelineDotnetTestVariables.ResultsDirectory] = "coverage/backend",
                [PipelineDotnetTestVariables.StatusVariable] = "BACKEND_STATUS",
                [PipelineDotnetTestVariables.DotnetPath] = "$(DOTNET)"
            },
            timeoutSeconds: 60,
            onOutput: (line, _) =>
            {
                _output.Add(line);
                return Task.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("invalid-request", result.FailureCode);
        Assert.Contains("never published", result.FailureReason!, StringComparison.Ordinal);
        Assert.Null(PublishedStatus());
    }

    // --- Microsoft.Testing.Platform (global.json "test.runner") ---

    private void SelectTestingPlatform() => File.WriteAllText(
        Path.Combine(_workspace, "global.json"),
        """{ "sdk": { "version": "10.0.100" }, "test": { "runner": "Microsoft.Testing.Platform" } }""");

    /// <summary>A runner that records the argv it was given, one argument per line.</summary>
    private string RecordingRunner()
    {
        var path = Path.Combine(_workspace, OperatingSystem.IsWindows() ? "record.cmd" : "record.sh");
        File.WriteAllText(
            path,
            OperatingSystem.IsWindows()
                ? "@echo off\r\n(for %%a in (%*) do @echo %%~a) > \"%~dp0args.txt\"\r\n" + WindowsPublishStaged + "exit /b 0\r\n"
                : "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$(dirname \"$0\")/args.txt\"\n" + PosixPublishStaged + "exit 0\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>Verifies that the test executor selects the Microsoft.Testing.Platform or VSTest
    /// argument dialect from global.json.</summary>
    [Fact]
    public async Task OnTheTestingPlatform_TheRunnerReceivesItsCommandLine()
    {
        SelectTestingPlatform();
        _ = ResultsDirectory;
        Stage("backend.trx", Trx());
        Stage("coverage.cobertura.20260912101500123.xml", ValidCoverage);
        var executor = new PipelineDotnetTestOperationExecutor(
            NullLogger<PipelineDotnetTestOperationExecutor>.Instance, new PhysicalFileSystemReader());

        var result = await executor.ExecuteAsync(
            OperationKind.PipelineDotnetTest,
            "tests/Sample.Tests",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WORKSPACE"] = _workspace,
                [PipelineDotnetTestVariables.ResultsDirectory] = "coverage/backend",
                [PipelineDotnetTestVariables.StatusVariable] = "BACKEND_STATUS",
                [PipelineDotnetTestVariables.TrxName] = "backend.trx",
                [PipelineDotnetTestVariables.CollectCoverage] = "true",
                [PipelineDotnetTestVariables.DotnetPath] = RecordingRunner()
            },
            timeoutSeconds: 60,
            onOutput: (line, _) =>
            {
                _output.Add(line);
                return Task.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var arguments = (await File.ReadAllLinesAsync(Path.Combine(_workspace, "args.txt"), TestContext.Current.CancellationToken))
            .Select(line => line.Trim())
            .ToList();
        Assert.Equal(["test", "--project", "tests/Sample.Tests"], arguments.Take(3));
        Assert.Equal("backend.trx", arguments[arguments.IndexOf("--report-trx-filename") + 1]);
        Assert.Contains("--report-trx", arguments);
        Assert.Contains("--coverlet", arguments);
        Assert.DoesNotContain("--logger", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--collect", StringComparison.Ordinal));
    }

    /// <summary>
    /// coverlet.MTP stamps its report; the classifier requires coverage.cobertura.xml. Without the
    /// normalisation the run would be refused as "no Cobertura report" while one sat next to the TRX.
    /// </summary>
    [Fact]
    public async Task OnTheTestingPlatform_AStampedCoverageReportIsNormalizedThenGraded()
    {
        SelectTestingPlatform();
        var results = ResultsDirectory;
        Stage("coverage.cobertura.20260912101500123.xml", ValidCoverage);

        var result = await RunAsync(runnerExitCode: 0, trx: Trx(), collectCoverage: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0", PublishedStatus());
        Assert.True(File.Exists(Path.Combine(results, "20260912101500123", "coverage.cobertura.xml")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }
}
