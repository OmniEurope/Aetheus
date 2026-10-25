// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Recette R-17: OpenGrep writes its normal end-of-scan tables on stderr, which the process runner streams
/// at Error, so a clean SAST scan filled the Security stage with errors. The stderr is now logged at Info
/// when the scan ended normally and at Error otherwise, and the files skipped for their size are named.
/// These tests drive the real <see cref="ScannerProcessExecutionRunner"/> with a process runner that
/// replays stderr and stdout lines; nothing launches OpenGrep itself.
/// </summary>
public sealed class OpenGrepRunLogTests : IDisposable
{
    private const string StatusTable = "| Scan Status |";
    private const string SummaryLine = "Ran 4 rules on 4 files: 0 findings.";
    private const string SkipLine = "  Scan skipped: 1 files larger than 1.0 MB";

    private readonly string _root = Directory.CreateTempSubdirectory("aetheus-opengrep-log-").FullName;
    private readonly List<(string Message, TaskLogLevel Level)> _log = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ScannerManifestEntry OpenGrep => ScannerManifestCatalog.Find("opengrep")!;

    private Task Record(string message, TaskLogLevel level)
    {
        _log.Add((message, level));
        return Task.CompletedTask;
    }

    private async Task<(ExecutorResult Result, bool QuotaExceeded)> RunAsync(
        ScannerManifestEntry scanner, ReplayRunner replay, string? sourceDirectory = null)
    {
        var output = Directory.CreateDirectory(Path.Combine(_root, "output")).FullName;
        return await new ScannerProcessExecutionRunner(replay, TimeProvider.System).RunAsync(
            scanner, new ProcessStartInfo("docker"), Path.Combine(output, "report.sarif"), output, null, 60,
            Record, TestContext.Current.CancellationToken, sourceDirectory);
    }

    private IEnumerable<string> At(TaskLogLevel level) => _log.Where(line => line.Level == level).Select(line => line.Message);

    [Fact]
    public async Task ACleanScan_LogsItsStderrTablesAtInfo()
    {
        var (result, _) = await RunAsync(OpenGrep, new ReplayRunner(0, false, [StatusTable, SummaryLine], ["scanning"]));

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(At(TaskLogLevel.Error));
        Assert.Contains(StatusTable, At(TaskLogLevel.Info));
        Assert.Contains(SummaryLine, At(TaskLogLevel.Info));
        Assert.Contains("scanning", At(TaskLogLevel.Info));
    }

    [Fact]
    public async Task AScanThatFoundIssues_ExitingWithAFindingCode_StillLogsItsStderrAtInfo()
    {
        Assert.Contains(1, OpenGrep.FindingExitCodes);

        await RunAsync(OpenGrep, new ReplayRunner(1, false, [StatusTable, "Ran 4 rules on 4 files: 4 findings."], []));

        Assert.Empty(At(TaskLogLevel.Error));
        Assert.Contains("Ran 4 rules on 4 files: 4 findings.", At(TaskLogLevel.Info));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public async Task AFailedScan_KeepsItsStderrAtError(int exitCode, bool timedOut)
    {
        await RunAsync(OpenGrep, new ReplayRunner(exitCode, timedOut, ["Error: invalid rule file"], []));

        Assert.Equal(["Error: invalid rule file"], At(TaskLogLevel.Error));
        Assert.Empty(At(TaskLogLevel.Info));
    }

    [Fact]
    public async Task ACancelledScan_FlushesTheHeldStderrAtError()
    {
        var replay = new ReplayRunner(0, false, ["partial output"], []) { Cancel = true };

        await Assert.ThrowsAsync<OperationCanceledException>(() => RunAsync(OpenGrep, replay));

        Assert.Equal(["partial output"], At(TaskLogLevel.Error));
    }

    [Fact]
    public async Task AnotherScanner_KeepsItsStderrAtErrorEvenOnASuccessfulExit()
    {
        var gitleaks = ScannerManifestCatalog.Find("gitleaks")!;

        await RunAsync(gitleaks, new ReplayRunner(0, false, ["gitleaks notice"], []));

        Assert.Equal(["gitleaks notice"], At(TaskLogLevel.Error));
    }

    [Fact]
    public async Task AFloodOfStderr_IsNotHeldWithoutBound()
    {
        var lines = Enumerable.Range(0, OpenGrepRunLog.MaxHeldLines + 2).Select(index => $"line {index}").ToArray();

        await RunAsync(OpenGrep, new ReplayRunner(0, false, lines, []));

        Assert.Equal(["line 5000", "line 5001"], At(TaskLogLevel.Error));
        Assert.Equal(OpenGrepRunLog.MaxHeldLines, At(TaskLogLevel.Info).Count());
    }

    [Fact]
    public async Task ASizeSkip_NamesTheOversizedFilesOfTheSource_ExceptTheExcludedOnes()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        Write(source, "big/data.json", 1_000_001);
        Write(source, "small.txt", 10);
        Write(source, "exactly-the-limit.txt", 1_000_000);
        Write(source, "docs/audit/360/metrics/complexity_full.txt", 2_000_000);
        Write(source, "node_modules/pkg/huge.js", 3_000_000);
        Write(source, "src/Aetheus.Front/wwwroot/lib/editor.js", 4_000_000);
        Write(source, "Data/Model.Designer.cs", 1_500_000);

        await RunAsync(OpenGrep, new ReplayRunner(0, false, [StatusTable, SkipLine], []), source);

        var warning = Assert.Single(At(TaskLogLevel.Warning));
        Assert.Contains("OpenGrep skipped 1 file(s) larger than 1000000 bytes", warning, StringComparison.Ordinal);
        Assert.Contains("big/data.json (1000001 bytes)", warning, StringComparison.Ordinal);
        foreach (var absent in new[] { "small.txt", "exactly-the-limit", "complexity_full", "huge.js", "editor.js", "Designer" })
            Assert.DoesNotContain(absent, warning, StringComparison.Ordinal);
        Assert.Empty(At(TaskLogLevel.Error));
    }

    [Fact]
    public async Task ASizeSkip_ListsTheBiggestFilesFirstAndCapsTheList()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        for (var index = 0; index < 22; index++)
            Write(source, $"f{index:00}.bin", 1_000_001 + index);

        await RunAsync(OpenGrep, new ReplayRunner(0, false, ["Scan skipped: 22 files larger than 1.0 MB"], []), source);

        var warning = Assert.Single(At(TaskLogLevel.Warning));
        Assert.StartsWith("OpenGrep skipped 22 file(s)", warning, StringComparison.Ordinal);
        Assert.True(warning.IndexOf("f21.bin", StringComparison.Ordinal) < warning.IndexOf("f20.bin", StringComparison.Ordinal));
        Assert.Contains("f02.bin", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("f01.bin", warning, StringComparison.Ordinal);
        Assert.EndsWith(", and 2 more.", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASizeSkip_WithNoCandidateInTheSource_SaysSo()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        Write(source, "small.txt", 10);

        await RunAsync(OpenGrep, new ReplayRunner(0, false, [SkipLine], []), source);

        var warning = Assert.Single(At(TaskLogLevel.Warning));
        Assert.Contains("No such file was found in the scanned source outside the excluded paths.", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutASizeSkipLine_NothingIsReportedAsSkipped()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        Write(source, "big.bin", 2_000_000);

        await RunAsync(OpenGrep, new ReplayRunner(0, false, [StatusTable, SummaryLine], []), source);

        Assert.Empty(At(TaskLogLevel.Warning));
    }

    [Fact]
    public async Task ASizeSkip_OfAFailedScan_IsNotReported()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        Write(source, "big.bin", 2_000_000);

        await RunAsync(OpenGrep, new ReplayRunner(2, false, [SkipLine], []), source);

        Assert.Empty(At(TaskLogLevel.Warning));
        Assert.Equal([SkipLine], At(TaskLogLevel.Error));
    }

    [Theory]
    [InlineData("Scan skipped: 1 files larger than 1.0 MB", 1, 1_000_000L)]
    [InlineData("  Scan skipped: 3 files larger than 500 KB", 3, 500_000L)]
    [InlineData("Scan skipped: 2 files larger than 2.5 GB", 2, 2_500_000_000L)]
    [InlineData("Scan skipped: 1 file larger than 1.5 MB", 1, 1_500_000L)]
    public void TheSizeSkipLine_GivesTheCountAndTheLimit(string line, int count, long limit) =>
        Assert.Equal((count, limit), OpenGrepSkippedFiles.ParseSizeSkip(["Scan Summary", line]));

    [Theory]
    [InlineData("Ran 4 rules on 4 files: 0 findings.")]
    [InlineData("Scan skipped: 1 files matching .semgrepignore patterns")]
    public void AnotherLine_IsNotASizeSkip(string line) =>
        Assert.Null(OpenGrepSkippedFiles.ParseSizeSkip([line]));

    [Theory]
    [InlineData("node_modules", "a/node_modules/b.js", true)]
    [InlineData("node_modules", "a/not_node_modules/b.js", false)]
    [InlineData("src/Aetheus.Front/wwwroot/lib", "src/Aetheus.Front/wwwroot/lib/x.js", true)]
    [InlineData("src/Aetheus.Front/wwwroot/lib", "other/src/Aetheus.Front/wwwroot/lib/x.js", false)]
    [InlineData("*.Designer.cs", "a/B.Designer.cs", true)]
    [InlineData("*.Designer.cs", "a/B.Designer.cs.txt", false)]
    [InlineData("coverage*", "coverage-report/x", true)]
    [InlineData("coverage*", "mycoverage/x", false)]
    [InlineData("site", "site/a.png", true)]
    [InlineData("site", "website/a.png", false)]
    [InlineData("docs/audit/360/metrics/complexity_full.txt", "docs/audit/360/metrics/complexity_full.txt", true)]
    [InlineData("docs/audit/360/metrics/complexity_full.txt", "docs/audit/360/metrics/complexity_warnings.txt", false)]
    public void AnExcludePattern_CoversWhatOpenGrepWouldSkip(string pattern, string path, bool excluded) =>
        Assert.Equal(excluded, OpenGrepSkippedFiles.ToExcludeRegex(pattern).IsMatch(path));

    [Fact]
    public void TheManifestExcludes_AreReadFromTheScannerArguments() =>
        Assert.Contains("docs/audit/360/metrics/complexity_full.txt", OpenGrepSkippedFiles.ExcludePatterns(OpenGrep.Arguments));

    private static void Write(string root, string relativePath, int bytes)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.SetLength(bytes);
    }

    /// <summary>A scanner process that writes the given lines on stdout (Info) and stderr (Error), then
    /// ends with the given outcome, or is cancelled after writing.</summary>
    private sealed class ReplayRunner(int exitCode, bool timedOut, string[] stderr, string[] stdout) : IScannerProcessRunner
    {
        public bool Cancel { get; init; }

        public async Task<ExecutorResult> RunAsync(
            ProcessStartInfo process, int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
        {
            foreach (var line in stdout)
                await onOutput(line, TaskLogLevel.Info);
            foreach (var line in stderr)
                await onOutput(line, TaskLogLevel.Error);
            if (Cancel)
                throw new OperationCanceledException(ct);
            return new ExecutorResult(exitCode, timedOut);
        }
    }
}
