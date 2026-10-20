// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/record-storage-evidence.sh</c>, run through the real executor with
/// docker and df answered by <see cref="FakeFunctionTools"/> and a fixed clock: the ledger it keeps,
/// the residual QA stacks it refuses, the plateau it verifies, and the script's refusals with its
/// messages.
/// </summary>
public sealed class RecordStorageEvidenceFunctionTests : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;
    private const string Df = "Filesystem 1024-blocks Used Available Capacity Mounted on\n/dev/sda1 104857600 52428800 52428800 50% /";

    private readonly FunctionTestHarness _harness = new("aetheus-storage-evidence");
    private readonly FakeFunctionTools _tools = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 5, 6, 7, TimeSpan.Zero));
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _env;
    private readonly string _work;

    private string _buildx = "Reclaimable:\t1GB\nTotal:\t2GB";
    private string _containers = string.Empty;
    private string _volumes = string.Empty;
    private string _df = Df;
    private int _buildxExit;

    public RecordStorageEvidenceFunctionTests()
    {
        _work = Directory.CreateDirectory(Path.Combine(_harness.Root, "work")).FullName;
        _env = new(StringComparer.Ordinal)
        {
            ["BUILD_BUILDID"] = "2514",
            ["BUILD_PROJECTNAME"] = "Aetheus",
            ["AETHEUS_BUILDX_BUILDER"] = "builder-a",
            ["AETHEUS_AGENT_WORK_DIRECTORY"] = _work,
            ["BUILDX_BUILDER"] = string.Empty,
            ["AETHEUS_STORAGE_MAX_CACHE_GIB"] = string.Empty,
            ["AETHEUS_STORAGE_PLATEAU_GROWTH_GIB"] = string.Empty
        };
        _tools.OnRun = (process, _) => Task.FromResult(Answer(process));
    }

    public void Dispose() => _harness.Dispose();

    private string Ledger(string scope = "qa") => Path.Combine(_work, "storage-evidence", scope + "-v2.tsv");

    private (int, string[]) Answer(ProcessStartInfo process)
    {
        static string[] Lines(string text) => text.Length == 0 ? [] : text.Split('\n');
        return process.ArgumentList[0] switch
        {
            "buildx" => (_buildxExit, Lines(_buildx)),
            "ps" => (0, Lines(_containers)),
            "volume" => (0, Lines(_volumes)),
            "-Pk" => (0, Lines(_df)),
            _ => (127, [])
        };
    }

    private void Seed(params string[] rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Ledger())!);
        File.WriteAllText(Ledger(), string.Concat(rows.Select(row => row + "\n")));
    }

    private static string Row(string run, string builder, long cache, int residualContainers = 0, int residualVolumes = 0) =>
        $"2026-10-01T00:00:00Z\t{run}\t{builder}\t{cache}\t0\t50\t100\t{residualContainers}\t{residualVolumes}";

    private async Task<Executors.ExecutorResult> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        var (result, _) = await _harness.RunAsync(new RecordStorageEvidenceFunction(_tools, _time), _env);
        return result;
    }

    private async Task<string> FailureAsync()
    {
        var result = await RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        return result.FailureReason!;
    }

    private bool Logged(string text) => _harness.Log.Any(entry => entry.Line.Contains(text, StringComparison.Ordinal));

    [Fact]
    public async Task FirstSample_IsRecordedAndWaitsForFourMore()
    {
        var result = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("2026-10-14T05:06:07Z\t2514\tbuilder-a\t2000000000\t1000000000\t50\t53687091200\t0\t0\n", File.ReadAllText(Ledger()));
        Assert.True(Logged("Storage evidence recorded: scope=qa run=2514 builder=builder-a cache=2000000000 reclaimable=1000000000 diskUsed=50% free=53687091200 residualContainers=0 residualVolumes=0 samples=1/5"));
        Assert.True(Logged("Storage plateau evidence is waiting for 4 additional successful QA run(s)."));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Ledger()));
        Assert.Equal(["buildx", "du", "--builder", "builder-a"], _tools.Runs[0].ArgumentList);
    }

    [Fact]
    public async Task FiveSamplesWithinBudget_VerifyThePlateau()
    {
        Seed(Row("2510", "builder-a", 2_000_000_000), Row("2511", "builder-a", 2_000_000_000),
            Row("2512", "builder-a", 2_000_000_000), Row("2513", "builder-a", 2_000_000_000));

        var result = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.True(Logged("Storage plateau evaluation: recentRangeBytes=0 maxAllowedBytes=5368709120 cacheBudgetBytes=85899345920 residual=0"));
        Assert.True(Logged("Storage plateau verified across five successful QA runs."));
    }

    [Fact]
    public async Task ASampleOverTheBudget_FailsThePlateau()
    {
        Seed(Row("2510", "builder-a", 81 * GiB), Row("2511", "builder-a", 2_000_000_000),
            Row("2512", "builder-a", 2_000_000_000), Row("2513", "builder-a", 2_000_000_000));

        Assert.Equal($"Function 'record-storage-evidence' failed: Storage plateau verification failed; inspect {Ledger()} before any production rollout.", await FailureAsync());
    }

    [Fact]
    public async Task GrowthOverTheLastThreeSamples_FailsThePlateau()
    {
        Seed(Row("2510", "builder-a", 1), Row("2511", "builder-a", 1),
            Row("2512", "builder-a", 8 * GiB), Row("2513", "builder-a", 2 * GiB));

        await FailureAsync();

        Assert.True(Logged("recentRangeBytes=6589934592 maxAllowedBytes=5368709120"));
    }

    [Fact]
    public async Task AResidualInARecentSample_FailsThePlateau()
    {
        Seed(Row("2510", "builder-a", 2_000_000_000), Row("2511", "builder-a", 2_000_000_000),
            Row("2512", "builder-a", 2_000_000_000), Row("2513", "builder-a", 2_000_000_000, residualVolumes: 1));

        await FailureAsync();

        Assert.True(Logged("residual=1"));
    }

    [Fact]
    public async Task ThresholdParameters_WinOverTheVariables()
    {
        Seed(Row("2510", "builder-a", 2_000_000_000), Row("2511", "builder-a", 2_000_000_000),
            Row("2512", "builder-a", 2_000_000_000), Row("2513", "builder-a", 2_000_000_000));
        _env["AETHEUS_STORAGE_MAX_CACHE_GIB"] = "80";
        _parameters["max_cache_gib"] = "1";

        await FailureAsync();

        Assert.True(Logged("cacheBudgetBytes=1073741824"));
    }

    [Fact]
    public async Task Rerun_ReplacesItsRowAndTheLedgerKeepsFiftyRows()
    {
        var rows = Enumerable.Range(0, 55).SelectMany(index => new[]
        {
            Row((1000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture), "builder-b", 5),
            Row((2000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture), "builder-a", 2_000_000_000)
        }).Append(Row("2514", "builder-a", 999)).ToArray();
        Seed(rows);

        Assert.Equal(0, (await RunAsync()).ExitCode);

        var kept = File.ReadAllLines(Ledger());
        Assert.Equal(50, kept.Length);
        Assert.Single(kept, line => line.Split('\t')[1] == "2514");
        Assert.EndsWith("\t2514\tbuilder-a\t2000000000\t1000000000\t50\t53687091200\t0\t0", kept[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("c1\taetheus-qa-rollback-2514-back\taetheus-qa-rollback-2514", "", 1, 0)]
    [InlineData("", "db\taetheus-qa-2513\nx\taetheus-qa-2512", 0, 2)]
    public async Task AStackOfThisRunOrAnOlderOne_IsRefusedAndNothingIsRecorded(string containers, string volumes, int expectedContainers, int expectedVolumes)
    {
        _containers = containers;
        _volumes = volumes;

        Assert.EndsWith($"{expectedContainers} residual QA container(s) and {expectedVolumes} volume(s) of this run or an older one remain on the host.", await FailureAsync(), StringComparison.Ordinal);
        Assert.True(Logged("Exact residual QA Compose resources detected:"));
        Assert.False(File.Exists(Ledger()));
    }

    [Fact]
    public async Task ANewerRunStack_IsReportedNotCounted()
    {
        _containers = "c1\taetheus-qa-rollback-2515-back\taetheus-qa-rollback-2515";
        _volumes = "v\taetheus-qa-rollback-2515";

        Assert.Equal(0, (await RunAsync()).ExitCode);
        Assert.Equal(2, _harness.Log.Count(entry => entry.Level == TaskLogLevel.Warning
            && entry.Line == "Not counted: 1 resource(s) of a newer QA run still in progress."));
    }

    [Fact]
    public async Task ANonNumericRunId_CountsEveryQaStack()
    {
        _parameters["run_id"] = "nightly-7";
        _containers = "c1\taetheus-qa-rollback-2515-back\taetheus-qa-rollback-2515";

        await FailureAsync();
    }

    [Fact]
    public async Task StacksAreNamedAfterTheProject()
    {
        _containers = "c1\tb\taetheus-qa-rollback-2500\nc2\tb\tastraia-qa-rollback-x";
        _parameters["project_name"] = "Astraia";

        Assert.Equal(0, (await RunAsync()).ExitCode);

        _containers = "c1\tb\tastraia-qa-2500";
        await FailureAsync();
    }

    [Theory]
    [InlineData("2GB", "2000000000")]
    [InlineData("1.5GiB", "1610612736")]
    [InlineData("512MB", "512000000")]
    [InlineData("*1,25kB", "1250")]
    [InlineData("2,5TB", "2500000000000")]
    [InlineData("2.5B", "2")]
    [InlineData("3.5", "4")]
    [InlineData("7 GB", "7")]
    [InlineData("1KiB", "1024")]
    [InlineData("3XB", null)]
    [InlineData("", null)]
    [InlineData("GB", null)]
    public void SizeToBytes_ReadsTheUnitsTheScriptRead(string raw, string? expected) =>
        Assert.Equal(expected, RecordStorageEvidenceFunction.SizeToBytes(raw));

    [Fact]
    public async Task AnUnreadableTotal_IsRefusedWithTheScriptMessage()
    {
        _buildx = "Reclaimable:\t1GB";

        Assert.EndsWith("Could not parse Buildx total ''.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedBuildxInventory_IsRefused()
    {
        _buildxExit = 1;

        Assert.EndsWith("Could not inventory Buildx builder 'builder-a'.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableDf_FailsInsteadOfRecordingEmptyDiskValues()
    {
        _df = "df: /: No such file or directory";

        Assert.EndsWith("Could not measure the root filesystem with df -Pk /.", await FailureAsync(), StringComparison.Ordinal);
        Assert.False(File.Exists(Ledger()));
    }

    [Theory]
    [InlineData("scope", "q/a", "Storage evidence scope is invalid.")]
    [InlineData("run_id", "25 14", "Storage evidence run id is missing or invalid.")]
    [InlineData("max_cache_gib", "8x", "Storage evidence thresholds must be non-negative integers.")]
    [InlineData("plateau_growth_gib", "-1", "Storage evidence thresholds must be non-negative integers.")]
    [InlineData("project_name", "---", "BUILD_PROJECTNAME is missing or has no usable character; the QA stacks cannot be named.")]
    public async Task InvalidParameters_AreRefusedBeforeAnyInventory(string parameter, string value, string message)
    {
        _parameters[parameter] = value;

        Assert.EndsWith(message, await FailureAsync(), StringComparison.Ordinal);
        Assert.Empty(_tools.Runs);
    }

    [Fact]
    public async Task AMissingRunId_IsRefused()
    {
        _env["BUILD_BUILDID"] = string.Empty;

        Assert.EndsWith("Storage evidence run id is missing or invalid.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingBuilder_IsRefused_AndBuildxBuilderIsTheFallback()
    {
        _env["AETHEUS_BUILDX_BUILDER"] = string.Empty;
        Assert.EndsWith("AETHEUS_BUILDX_BUILDER is missing; refusing to attribute cache evidence to an unknown builder.", await FailureAsync(), StringComparison.Ordinal);

        _env["BUILDX_BUILDER"] = "builder-z";
        Assert.Equal(0, (await RunAsync()).ExitCode);
        Assert.Contains("\tbuilder-z\t", File.ReadAllText(Ledger()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingWorkDirectory_IsRefused()
    {
        _env["AETHEUS_AGENT_WORK_DIRECTORY"] = Path.Combine(_harness.Root, "nowhere");

        Assert.EndsWith("AETHEUS_AGENT_WORK_DIRECTORY is missing or unavailable; evidence would not survive workspace cleanup.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherScope_KeepsItsOwnLedger()
    {
        _parameters["scope"] = "nightly";

        Assert.Equal(0, (await RunAsync()).ExitCode);
        Assert.True(File.Exists(Ledger("nightly")));
        Assert.False(File.Exists(Ledger()));
    }
}
