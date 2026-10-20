// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Toolchains;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// A whole container step through <see cref="ContainerRunExecutor.ExecuteAsync"/>: the workspace and state
/// directories, the toolchain resolution, the step script written into the mounted workspace, the hardened
/// <c>docker run</c> argument list, the streamed output and exit code, the publication back and the purge of
/// the run's directories. <c>docker</c> is a real stand-in program that prints its arguments and the step
/// script it was asked to run, then exits with a chosen code.
/// </summary>
public sealed class ContainerRunExecutorStepTests : IDisposable
{
    private const int WorkspaceKey = 4242;
    private static readonly string ImmutableImage = $"debian:bookworm-slim@sha256:{new string('a', 64)}";

    private readonly string _root = Directory.CreateTempSubdirectory("aetheus-container-step-").FullName;
    private readonly OutputLog _log = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WorkDirectory => Path.Combine(_root, "work");
    private string Workspace => Path.Combine(WorkDirectory, "cw", WorkspaceKey.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private string StateRoot => Path.Combine(WorkDirectory, "container-state", WorkspaceKey.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private ContainerRunExecutor Executor(int exitCode) =>
        new(
            Options.Create(new AetheusAgentOptions { WorkDirectory = WorkDirectory }),
            NullLogger<ContainerRunExecutor>.Instance,
            new ToolchainResolver(),
            TimeProvider.System)
        {
            DockerProgram = StandInProgram.Write(_root, "docker",
                $"echo ARGS %*\r\ntype \"{Workspace}\\.prom-step-*.sh\"\r\nexit /b {exitCode}",
                $"echo ARGS \"$@\"\ncat \"{Workspace}\"/.prom-step-*.sh\nexit {exitCode}"),
        };

    private Task<ExecutorResult> RunStep(int exitCode, bool purge, Dictionary<string, string>? environment = null) =>
        Executor(exitCode).ExecuteAsync(
            new ContainerSpec { Image = ImmutableImage, WorkspaceKey = WorkspaceKey, Network = "none", PurgeWorkspace = purge },
            "echo step-body",
            environment ?? [],
            60,
            _log.Write,
            TestContext.Current.CancellationToken);

    private string DockerArguments() =>
        Assert.Single(_log.Lines, line => line.Message.StartsWith("ARGS ", StringComparison.Ordinal)).Message;

    [Fact]
    public async Task AStep_RunsItsScriptInAHardenedContainer_StreamsItsOutput_AndLeavesTheWorkspaceForTheNextStep()
    {
        var result = await RunStep(exitCode: 0, purge: false, new Dictionary<string, string> { ["PLAIN"] = "value" });

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Null(result.FailureCode);
        var arguments = DockerArguments();
        Assert.StartsWith("ARGS run --rm --name prom-", arguments, StringComparison.Ordinal);
        foreach (var hardening in new[] { "--cap-drop ALL", "--security-opt no-new-privileges", "--pids-limit 4096", "--read-only", "--network none", "--env-file " })
            Assert.Contains(hardening, arguments, StringComparison.Ordinal);
        Assert.Contains($"-- {ImmutableImage} /w/.prom-step-", arguments, StringComparison.Ordinal);
        Assert.True(_log.Has("echo step-body", TaskLogLevel.Info));
        Assert.True(_log.Has("\"image\":", TaskLogLevel.Info));
        // The workspace stays for the next step; the script and the env file are gone with this one.
        Assert.True(Directory.Exists(Workspace));
        Assert.Empty(Directory.EnumerateFiles(Workspace, ".prom-step-*.sh"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(StateRoot, "secrets")));
    }

    [Fact]
    public async Task TheRunsCleanupStep_ReturnsTheContainerExitCode_AndPurgesTheWorkspaceAndState()
    {
        var result = await RunStep(exitCode: 7, purge: true);

        Assert.Equal(7, result.ExitCode);
        Assert.Null(result.FailureCode);
        Assert.DoesNotContain("--env-file", DockerArguments(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Workspace));
        Assert.False(Directory.Exists(StateRoot));
    }
}
