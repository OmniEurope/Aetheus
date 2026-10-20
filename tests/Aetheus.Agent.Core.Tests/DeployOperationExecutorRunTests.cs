// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// A whole deploy through <see cref="DeployOperationExecutor.ExecuteAsync"/>, under a temporary deploy root:
/// the request checks, the per-app lock, the download, the archive guards, the release directory, the flip of
/// <c>current</c>, the health gate, the rollback and the pruning of old releases. The shell is scripted, but
/// <c>ln -sfn</c> and <c>mv -T</c> really create and rename a directory link (a junction on Windows without
/// the symbolic-link privilege), so what <c>current</c> points at is read back from the file system.
/// </summary>
public sealed class DeployOperationExecutorRunTests : IDisposable
{
    private const string App = "shop";
    private const string HealthyUnit = "ActiveState=active\nSubState=running\nNRestarts=0\n";
    private const string FailedUnit = "ActiveState=failed\nSubState=failed\nNRestarts=0\n";

    private readonly string _root = Directory.CreateTempSubdirectory("aetheus-deploy-run-").FullName;
    private readonly IServerApiClient _api = Substitute.For<IServerApiClient>();
    private readonly List<(string Message, TaskLogLevel Level)> _output = [];

    private string AppDir => Path.Combine(_root, App);
    private string ReleasesRoot => Path.Combine(AppDir, "releases");
    private string CurrentLink => Path.Combine(AppDir, "current");

    public void Dispose()
    {
        foreach (var link in new[] { CurrentLink, Path.Combine(AppDir, "current.tmp") })
            if (Directory.Exists(link)) DirectoryLinks.Remove(link);
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("not-linux", "Deploy operations are only supported on Linux")]
    [InlineData("bad-app", "Invalid deploy app name '../shop'")]
    [InlineData("bad-health-url", "Invalid AETHEUS_DEPLOY_HEALTH_URL")]
    [InlineData("missing-ids", "Missing AETHEUS_DEPLOY_ARTIFACT_ID / AETHEUS_DEPLOY_RUN_ID")]
    [InlineData("unsafe-compose", "Invalid or unsafe compose path '../compose.yml'")]
    public async Task ARejectedRequest_FailsBeforeAnyDownloadOrDirectory(string defect, string expected)
    {
        var env = DeployEnv(runId: 6);
        var target = App;
        var isLinux = true;
        switch (defect)
        {
            case "not-linux": isLinux = false; break;
            case "bad-app": target = "../shop"; break;
            case "bad-health-url": env["AETHEUS_DEPLOY_HEALTH_URL"] = "http://example.com/health"; break;
            case "missing-ids": env.Remove("AETHEUS_DEPLOY_RUN_ID"); break;
            case "unsafe-compose":
                env["AETHEUS_DEPLOY_KIND"] = "container";
                env["AETHEUS_DEPLOY_COMPOSE"] = "../compose.yml";
                break;
        }

        var result = await Executor(new LinkingShell(_ => HealthyUnit), isLinux: isLinux).ExecuteAsync(
            OperationKind.PipelineDeploy, target, env, 60, Write, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(_output, line => line.Level == TaskLogLevel.Error && line.Message.StartsWith(expected, StringComparison.Ordinal));
        Assert.False(Directory.Exists(AppDir));
        await _api.DidNotReceive().DownloadArtifactAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AGreenBinaryDeploy_FlipsCurrentOntoTheLauncher_WritesTheAppEnv_AndPrunesBeyondTheKeptReleases()
    {
        var previous = SeedReleases(1, 2, 3, 4, 5);
        DirectoryLinks.Create(CurrentLink, previous);
        ServeArchive(BinaryArchive());
        var shell = new LinkingShell(_ => HealthyUnit);
        var env = DeployEnv(runId: 6);
        env["AETHEUS_DEPLOY_APPENV_OTEL_SERVICE_NAME"] = "shop-api";

        var result = await Executor(shell).ExecuteAsync(
            OperationKind.PipelineDeploy, App, env, 60, Write, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var appRoot = Path.Combine(ReleasesRoot, "6", "publish", App);
        Assert.Equal(appRoot, DeployReleaseFileSystem.ReadLinkTarget(CurrentLink));
        Assert.Equal("OTEL_SERVICE_NAME=shop-api\n", await File.ReadAllTextAsync(Path.Combine(appRoot, ".aetheus-env"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(appRoot, "run")));
        // Kept: the new release, the previous one (protected) and the three newest others; release 1 goes.
        Assert.Equal(["2", "3", "4", "5", "6"], ReleaseNames());
        Assert.Contains(_output, line => line.Message == "Pruned old release 1");
        Assert.Contains(_output, line => line.Message == $"Deploy succeeded - aetheus-app@{App} is active");
        Assert.Contains(shell.Calls, call => call is ["sudo", "-n", DeployOperationExecutor.RestartHelperPath, App]);
        Assert.Contains(shell.Calls, call => call is ["systemctl", "show", "aetheus-app@shop.service", ..]);
        Assert.DoesNotContain(_output, line => line.Message.Contains("DEPLOY_ROLLED_BACK", StringComparison.Ordinal));
        await _api.Received(1).DownloadArtifactAsync(11, 6, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AReleaseThatFailsTheHealthGate_IsRolledBack_AndNothingIsPruned()
    {
        var previous = SeedReleases(1, 2, 3, 4, 5);
        DirectoryLinks.Create(CurrentLink, previous);
        ServeArchive(BinaryArchive());
        // The unit stays up only while current points at the previous release.
        var shell = new LinkingShell(_ =>
            DeployReleaseFileSystem.ReadLinkTarget(CurrentLink) == previous ? HealthyUnit : FailedUnit);

        var result = await Executor(shell).ExecuteAsync(
            OperationKind.PipelineDeploy, App, DeployEnv(runId: 6), 60, Write, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(previous, DeployReleaseFileSystem.ReadLinkTarget(CurrentLink));
        Assert.Contains(_output, line => line.Message == "Health gate failed - rolling back to 5");
        Assert.Contains(_output, line => line.Message == "##aetheus[setvariable name=DEPLOY_ROLLED_BACK]true");
        Assert.Equal(["1", "2", "3", "4", "5", "6"], ReleaseNames());
        Assert.Equal(2, shell.Calls.Count(call => call is ["sudo", ..]));
    }

    [Fact]
    public async Task ARerunOfTheLiveRunId_UnpacksIntoAFreshSibling_NeverIntoTheLiveRelease()
    {
        var live = SeedReleases(7);
        await File.WriteAllTextAsync(Path.Combine(live, "marker"), "live", TestContext.Current.CancellationToken);
        DirectoryLinks.Create(CurrentLink, live);
        ServeArchive(BinaryArchive(nested: false));

        var result = await Executor(new LinkingShell(_ => HealthyUnit)).ExecuteAsync(
            OperationKind.PipelineDeploy, App, DeployEnv(runId: 7), 60, Write, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var current = DeployReleaseFileSystem.ReadLinkTarget(CurrentLink)!;
        Assert.Equal(ReleasesRoot, Path.GetDirectoryName(current));
        Assert.Matches(@"^7__[0-9a-f]{8}$", Path.GetFileName(current));
        Assert.Equal("live", await File.ReadAllTextAsync(Path.Combine(live, "marker"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(live, "run")));
    }

    [Fact]
    public async Task AnArchiveWithoutARunLauncher_FailsWithoutTouchingCurrent()
    {
        ServeArchive(Archive(("publish/readme.txt", "no launcher")));

        var result = await Executor(new LinkingShell(_ => HealthyUnit)).ExecuteAsync(
            OperationKind.PipelineDeploy, App, DeployEnv(runId: 6), 60, Write, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(_output, line => line.Message.StartsWith("Artifact has no (unique) 'run' launcher", StringComparison.Ordinal));
        Assert.False(Directory.Exists(CurrentLink));
        Assert.True(File.Exists(Path.Combine(ReleasesRoot, "6", "publish", "readme.txt")));
    }

    [Fact]
    public async Task AnArtifactTheServerDoesNotHandOver_FailsAfterTakingTheLock()
    {
        _api.DownloadArtifactAsync(11, 6, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Stream?>(null));

        var result = await Executor(new LinkingShell(_ => HealthyUnit)).ExecuteAsync(
            OperationKind.PipelineDeploy, App, DeployEnv(runId: 6), 60, Write, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(_output, line => line.Message == "Artifact download failed (not found / forbidden)");
        Assert.True(File.Exists(Path.Combine(AppDir, ".lock")));
        Assert.False(Directory.Exists(ReleasesRoot));
    }

    [Fact]
    public async Task ACorruptArchive_IsReportedAsAFailedDeploy()
    {
        ServeArchive([1, 2, 3, 4]);

        var result = await Executor(new LinkingShell(_ => HealthyUnit)).ExecuteAsync(
            OperationKind.PipelineDeploy, App, DeployEnv(runId: 6), 60, Write, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(_output, line => line.Level == TaskLogLevel.Error && line.Message.StartsWith("Deploy failed: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotEnoughFreeSpace_RefusesBeforeExtractingAnything()
    {
        ServeArchive(BinaryArchive());

        var result = await Executor(new LinkingShell(_ => HealthyUnit), minFreeSpaceMiB: int.MaxValue).ExecuteAsync(
            OperationKind.PipelineDeploy, App, DeployEnv(runId: 6), 60, Write, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(_output, line => line.Message.StartsWith("Insufficient free space to deploy", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(ReleasesRoot, "6")));
    }

    [Fact]
    public async Task ADeployOfTheSameAppAlreadyHoldingTheLock_GivesUpAfterItsAttempts()
    {
        Directory.CreateDirectory(AppDir);
        await using var held = new FileStream(Path.Combine(AppDir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var result = await Executor(new LinkingShell(_ => HealthyUnit)).ExecuteAsync(
            OperationKind.PipelineDeploy, App, DeployEnv(runId: 6), 60, Write, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(_output, line => line.Message == "Another deploy of this app is in progress (lock timeout)");
        await _api.DidNotReceive().DownloadArtifactAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFirstReleaseThatCrashLoops_FailsTheGate_AndSaysTheAppIsDown()
    {
        ServeArchive(BinaryArchive());
        var samples = 0;
        var shell = new LinkingShell(_ => $"ActiveState=active\nSubState=running\nNRestarts={samples++}\n");

        var result = await Executor(shell).ExecuteAsync(
            OperationKind.PipelineDeploy, App, DeployEnv(runId: 6), 60, Write, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(_output, line => line.Message == "Health gate failed - aetheus-app@shop.service crash-looped (1 auto-restart(s) during stabilization)");
        Assert.Contains(_output, line => line.Message == "Health gate failed - no previous release to roll back to; app is DOWN");
        Assert.Equal(2, samples);
    }

    [Fact]
    public async Task ARestartHelperThatFails_FailsTheGateWithoutReadingTheUnit()
    {
        ServeArchive(BinaryArchive());
        var shell = new LinkingShell(_ => HealthyUnit, restartExitCode: 1);

        var result = await Executor(shell).ExecuteAsync(
            OperationKind.PipelineDeploy, App, DeployEnv(runId: 6), 60, Write, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(_output, line => line.Message == "Restart helper failed: restart refused");
        Assert.DoesNotContain(shell.Calls, call => call is ["systemctl", ..]);
    }

    [Theory]
    [InlineData(200, 0)]
    [InlineData(503, 1)]
    public async Task AHealthUrl_GatesTheReleaseOnItsHttpAnswer(int status, int expectedExit)
    {
        const string url = "http://127.0.0.1:8080/health";
        var readiness = new ReadinessAnswers((System.Net.HttpStatusCode)status);
        ServeArchive(BinaryArchive());
        var env = DeployEnv(runId: 6);
        env["AETHEUS_DEPLOY_HEALTH_URL"] = url;

        var result = await Executor(new LinkingShell(_ => HealthyUnit), readiness: readiness).ExecuteAsync(
            OperationKind.PipelineDeploy, App, env, 60, Write, TestContext.Current.CancellationToken);

        Assert.Equal(expectedExit, result.ExitCode);
        Assert.NotEmpty(readiness.Requests);
        Assert.All(readiness.Requests, request => Assert.Equal(url, request));
        if (expectedExit == 0)
            Assert.Contains(_output, line => line.Message == $"Functional readiness passed: {url}");
        else
            Assert.Contains(_output, line => line.Message ==
                $"Health gate failed - aetheus-app@shop.service did not hold a stable active(running) state with HTTP 2xx readiness from {url} in ~20s");
    }

    [Fact]
    public async Task AContainerDeploy_WithoutAReachableDockerDaemon_FailsHonestlyAfterUnpacking()
    {
        ServeArchive(Archive(("compose.yml", "services: {}\n"), ("image.tar", "tar")));
        var env = DeployEnv(runId: 6);
        env["AETHEUS_DEPLOY_KIND"] = "container";
        env["AETHEUS_DEPLOY_COMPOSE"] = "compose.yml";
        var shell = new LinkingShell(_ => HealthyUnit);

        var result = await Executor(shell).ExecuteAsync(
            OperationKind.PipelineDeploy, App, env, 60, Write, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(_output, line => line.Message.StartsWith("Docker daemon not reachable for the agent user", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(ReleasesRoot, "6", "compose.yml")));
        Assert.DoesNotContain(shell.Calls, call => call is ["docker", "load", ..]);
    }

    private DeployOperationExecutor Executor(
        LinkingShell shell, bool isLinux = true, int minFreeSpaceMiB = 0, ReadinessAnswers? readiness = null) =>
        new(_api, shell, Options.Create(new AetheusAgentOptions { DeployMinFreeSpaceMiB = minFreeSpaceMiB }),
            NullLogger<DeployOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            DeployRoot = _root,
            HealthPollInterval = TimeSpan.FromMilliseconds(1),
            LockRetryDelay = TimeSpan.FromMilliseconds(1),
            CreateReadinessHandler = () => new ReadinessHandler(readiness ?? new ReadinessAnswers(System.Net.HttpStatusCode.OK)),
        };

    private Task Write(string message, TaskLogLevel level)
    {
        _output.Add((message, level));
        return Task.CompletedTask;
    }

    private static Dictionary<string, string> DeployEnv(int runId) => new()
    {
        ["AETHEUS_DEPLOY_ARTIFACT_ID"] = "11",
        ["AETHEUS_DEPLOY_RUN_ID"] = runId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["AETHEUS_DEPLOY_KIND"] = "binary",
    };

    // Older releases, oldest first, each one older than the next.
    private string SeedReleases(params int[] runIds)
    {
        string last = string.Empty;
        for (var i = 0; i < runIds.Length; i++)
        {
            last = Directory.CreateDirectory(Path.Combine(ReleasesRoot, runIds[i].ToString(System.Globalization.CultureInfo.InvariantCulture))).FullName;
            Directory.SetLastWriteTimeUtc(last, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i));
        }
        return last;
    }

    private string[] ReleaseNames() =>
        [.. Directory.EnumerateDirectories(ReleasesRoot).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    private void ServeArchive(byte[] bytes) =>
        _api.DownloadArtifactAsync(11, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(new MemoryStream(bytes)));

    private static byte[] BinaryArchive(bool nested = true) =>
        nested
            ? Archive(($"publish/{App}/run", "#!/bin/sh\n"), ($"publish/{App}/app.dll", "dll"))
            : Archive(("run", "#!/bin/sh\n"), ("app.dll", "dll"));

    private static byte[] Archive(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>What the readiness endpoint answers, and every URL the probe asked; shared by the handler each probe creates.</summary>
    private sealed class ReadinessAnswers(System.Net.HttpStatusCode status)
    {
        internal System.Net.HttpStatusCode Status => status;
        internal System.Collections.Concurrent.ConcurrentQueue<string> Requests { get; } = new();
    }

    private sealed class ReadinessHandler(ReadinessAnswers answers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            answers.Requests.Enqueue(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(answers.Status));
        }
    }

    /// <summary>
    /// Scripted shell whose <c>ln -sfn</c> and <c>mv -T</c> really create and rename the link, whose
    /// <c>systemctl show</c> answers from <paramref name="unitState"/>, and whose <c>docker</c> is unreachable.
    /// </summary>
    private sealed class LinkingShell(Func<string[], string> unitState, int restartExitCode = 0) : IShellRunner
    {
        internal List<string[]> Calls { get; } = [];

        [Obsolete("Matches the interface; the executor never uses the shell form.")]
        public Task<string> RunAsync(string command, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromResult(string.Empty);

        public Task<string> RunWithStdinAsync(
            string fileName, IReadOnlyList<string> args, string stdin, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromResult(string.Empty);

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        {
            string[] call = [fileName, .. args];
            Calls.Add(call);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(call switch
            {
                ["ln", "-sfn", var target, var link] => Link(target, link),
                ["mv", "-T", var from, var to] => Rename(from, to),
                ["sudo", ..] => new ShellExecResult(restartExitCode, string.Empty, restartExitCode == 0 ? string.Empty : "restart refused"),
                ["systemctl", "show", ..] => new ShellExecResult(0, unitState(call), string.Empty),
                ["docker", ..] => new ShellExecResult(1, string.Empty, "Cannot connect to the Docker daemon"),
                _ => new ShellExecResult(0, string.Empty, string.Empty),
            });
        }

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environmentVariables,
            string workingDirectory, bool inheritEnvironment, int maxCapturedOutputBytes,
            CancellationToken ct, TimeSpan? timeout = null) =>
            RunExecAsync(fileName, args, ct, timeout);

        private static ShellExecResult Link(string target, string link)
        {
            if (Directory.Exists(link)) DirectoryLinks.Remove(link);
            DirectoryLinks.Create(link, target);
            return new ShellExecResult(0, string.Empty, string.Empty);
        }

        private static ShellExecResult Rename(string from, string to)
        {
            if (Directory.Exists(to)) DirectoryLinks.Remove(to);
            Directory.Move(from, to);
            return new ShellExecResult(0, string.Empty, string.Empty);
        }
    }
}
