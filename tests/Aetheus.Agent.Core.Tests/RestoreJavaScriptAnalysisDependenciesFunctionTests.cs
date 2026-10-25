// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the former <c>restore-javascript-analysis-dependencies.sh</c> script and of the
/// <c>ensure-node-runtime.sh</c> it loads, run through the real executor on a temp directory unique to
/// each test: the pinned Node.js taken from the host or installed under <c>$HOME/.aetheus</c> behind
/// the installation lock, the npm and lock-file checks, and the bounded <c>npm ci</c> retries.
/// </summary>
public sealed class RestoreJavaScriptAnalysisDependenciesFunctionTests : IDisposable
{
    private const string NodeVersion = "v" + PinnedNodeRuntime.Version;
    private static readonly byte[] ArchiveBytes = Encoding.UTF8.GetBytes("fake node archive");

    private readonly FunctionTestHarness _harness = new("aetheus-restore-js");
    private readonly FakeFunctionTools _tools = new();
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Queue<int> _npmExits = new();

    public RestoreJavaScriptAnalysisDependenciesFunctionTests()
    {
        _env["PATH"] = Path.Combine(_harness.Root, "bin");
        _env["HOME"] = Path.Combine(_harness.Root, "home");
        _tools.OnRun = Answer;
    }

    public void Dispose() => _harness.Dispose();

    private static PinnedDownload FakeArchive =>
        new(new Uri("https://example.invalid/node.tar.gz"), Convert.ToHexStringLower(SHA256.HashData(ArchiveBytes)));

    private string InstallDirectory => Path.Combine(_harness.Root, "home", ".aetheus", $"node-v{PinnedNodeRuntime.Version}-linux-x64");

    /// <summary>Any executable named node reports the pinned version; npm exits with the queued codes
    /// (0 once the queue is empty); tar lays out an extracted archive with bin/node and bin/npm.</summary>
    private Task<(int, string[])> Answer(ProcessStartInfo process, CancellationToken _)
    {
        var name = Path.GetFileName(process.FileName);
        if (name == "node")
            return Task.FromResult((0, new[] { NodeVersion }));
        if (name == "npm")
            return Task.FromResult((_npmExits.Count > 0 ? _npmExits.Dequeue() : 0, new[] { "npm output" }));
        if (name == "tar")
        {
            var staging = process.ArgumentList[^1];
            foreach (var tool in new[] { "node", "npm" })
            {
                var path = Path.Combine(staging, "bin", tool);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Empty);
                FunctionTestHarness.MakeExecutable(path);
            }
            return Task.FromResult((0, Array.Empty<string>()));
        }
        return Task.FromResult((127, Array.Empty<string>()));
    }

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync() =>
        _harness.RunAsync(new RestoreJavaScriptAnalysisDependenciesFunction(_tools, FakeArchive), _env);

    private static string FailureReason(Executors.ExecutorResult result)
    {
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        return result.FailureReason!;
    }

    private void HostNodeWithNpm()
    {
        _harness.Executable("bin/node");
        _harness.Executable("bin/npm");
    }

    private void CommittedLockFile() => _harness.Write("ws/package-lock.json", "{}");

    private IEnumerable<ProcessStartInfo> NpmRuns => _tools.Runs.Where(run => Path.GetFileName(run.FileName) == "npm");

    [Fact]
    public async Task PinnedHostNode_RunsNpmCiWithTheScriptsArguments()
    {
        HostNodeWithNpm();
        CommittedLockFile();

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(variables);
        var npm = Assert.Single(NpmRuns);
        Assert.Equal(Path.Combine(_harness.Root, "bin", "npm"), npm.FileName);
        Assert.Equal(
            ["ci", "--ignore-scripts", "--no-audit", "--no-fund", "--prefer-offline", "--fetch-retries=2",
             "--fetch-retry-mintimeout=1000", "--fetch-retry-maxtimeout=5000", "--fetch-timeout=60000"],
            npm.ArgumentList);
        Assert.Equal(_harness.Workspace, npm.WorkingDirectory);
        Assert.StartsWith(Path.Combine(_harness.Root, "bin") + Path.PathSeparator, npm.Environment["PATH"], StringComparison.Ordinal);
        Assert.Empty(_tools.Downloads);
    }

    [Fact]
    public async Task NodeWithoutNpm_FailsLikeTheScript()
    {
        _harness.Executable("bin/node");
        CommittedLockFile();

        var (result, _) = await RunAsync();

        Assert.EndsWith("The pinned Node.js runtime does not provide npm.", FailureReason(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingLockFile_FailsLikeTheScript()
    {
        HostNodeWithNpm();

        var (result, _) = await RunAsync();

        Assert.EndsWith("JavaScript analysis requires the committed root package-lock.json.", FailureReason(result), StringComparison.Ordinal);
        Assert.Empty(NpmRuns);
    }

    [Fact]
    public async Task FailedRestore_IsRetriedAfterFourThenSixSeconds()
    {
        HostNodeWithNpm();
        CommittedLockFile();
        _npmExits.Enqueue(1);
        _npmExits.Enqueue(1);

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, NpmRuns.Count());
        Assert.Equal([TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6)], _tools.Delays);
        Assert.Contains(_harness.Log, entry => entry.Line == "npm ci attempt 1 failed; retrying after a bounded backoff.");
        Assert.Contains(_harness.Log, entry => entry.Line == "npm ci attempt 2 failed; retrying after a bounded backoff.");
    }

    [Fact]
    public async Task ThreeFailedRestores_FailLikeTheScript()
    {
        HostNodeWithNpm();
        CommittedLockFile();
        foreach (var _ in Enumerable.Range(0, 3))
            _npmExits.Enqueue(1);

        var (result, _) = await RunAsync();

        Assert.EndsWith("npm ci failed after 3 bounded attempts.", FailureReason(result), StringComparison.Ordinal);
        Assert.Equal(3, NpmRuns.Count());
        Assert.Equal(2, _tools.Delays.Count);
    }

    [Fact]
    public async Task HostNodeOnAnotherVersion_OutsideLinuxX64_FailsLikeTheScript()
    {
        _harness.Executable("bin/node");
        _tools.OnRun = (_, _) => Task.FromResult((0, new[] { "v22.0.0" }));

        var (result, _) = await RunAsync();

        Assert.EndsWith($"Pinned Node.js {PinnedNodeRuntime.Version} bootstrap supports Linux x86_64 only", FailureReason(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoHostNode_WithoutHome_FailsLikeTheScript()
    {
        _tools.IsLinuxX64 = true;
        _env["HOME"] = string.Empty;

        var (result, _) = await RunAsync();

        Assert.EndsWith("HOME is required to install pinned Node.js without root", FailureReason(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoHostNode_WithoutTar_FailsLikeTheScript()
    {
        _tools.IsLinuxX64 = true;

        var (result, _) = await RunAsync();

        Assert.EndsWith($"tar is required to install Node.js {PinnedNodeRuntime.Version}", FailureReason(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoHostNode_InstallsThePinnedArchiveThenRestores()
    {
        _tools.IsLinuxX64 = true;
        _tools.DownloadContent = ArchiveBytes;
        _harness.Executable("bin/tar");
        CommittedLockFile();

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(FakeArchive.Source, Assert.Single(_tools.Downloads));
        var tar = _tools.Runs.Single(run => Path.GetFileName(run.FileName) == "tar");
        Assert.Equal("-xzf", tar.ArgumentList[0]);
        Assert.Equal(["--strip-components=1", "-C"], tar.ArgumentList.Skip(2).Take(2));
        Assert.Equal(Path.Combine(InstallDirectory, "bin", "npm"), Assert.Single(NpmRuns).FileName);
        Assert.True(File.Exists(Path.Combine(InstallDirectory, "bin", "node")));
        Assert.False(File.Exists(InstallDirectory + ".lock"), "The installation lock is released.");
        Assert.Equal([$"node-v{PinnedNodeRuntime.Version}-linux-x64"], Directory.GetFileSystemEntries(Path.GetDirectoryName(InstallDirectory)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ArchiveWithAnotherDigest_IsNeverExtracted()
    {
        _tools.IsLinuxX64 = true;
        _tools.DownloadContent = Encoding.UTF8.GetBytes("tampered");
        _harness.Executable("bin/tar");

        var (result, _) = await RunAsync();

        Assert.Contains("does not match its pinned SHA-256", FailureReason(result), StringComparison.Ordinal);
        Assert.DoesNotContain(_tools.Runs, run => Path.GetFileName(run.FileName) == "tar");
        Assert.False(Directory.Exists(InstallDirectory));
        Assert.False(File.Exists(InstallDirectory + ".lock"));
    }

    [Fact]
    public async Task LockHeldByTheScript_TimesOutAfterOneHundredTwentyAttempts()
    {
        _tools.IsLinuxX64 = true;
        _harness.Executable("bin/tar");
        Directory.CreateDirectory(InstallDirectory + ".lock");

        var (result, _) = await RunAsync();

        Assert.EndsWith($"Timed out waiting for the pinned Node.js {PinnedNodeRuntime.Version} installation lock", FailureReason(result), StringComparison.Ordinal);
        Assert.Equal(119, _tools.Delays.Count);
        Assert.Empty(_tools.Downloads);
        Assert.True(Directory.Exists(InstallDirectory + ".lock"), "Another holder's lock is never removed.");
    }

    [Fact]
    public void OfficialArchivePin_IsTheScriptsUrlAndDigest()
    {
        Assert.Equal($"https://nodejs.org/dist/v{PinnedNodeRuntime.Version}/node-v{PinnedNodeRuntime.Version}-linux-x64.tar.gz", PinnedNodeRuntime.OfficialArchive.Source.ToString());
        Assert.Equal(PinnedNodeRuntime.LinuxX64Sha256, PinnedNodeRuntime.OfficialArchive.Sha256);
    }
}
