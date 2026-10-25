// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Toolchains;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The part of a container step that starts the container process: secrets files written before the
/// start, output streaming, the exit code and the toolchain-mismatch code, the timeout that removes the
/// container, and the files removed afterwards. The "docker" process is a real script that prints, fails or
/// sleeps, so the whole path runs for real; only the Docker daemon call that removes a container is injected.</summary>
public sealed class ContainerRunExecutorRunContainerTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("aetheus-run-container-");
    private readonly List<string> _removedContainers = [];

    public void Dispose() => _dir.Delete(recursive: true);

    private ContainerRunExecutor NewExecutor() =>
        new(
            Options.Create(new AetheusAgentOptions()),
            NullLogger<ContainerRunExecutor>.Instance,
            new ToolchainResolver(),
            TimeProvider.System)
        {
            RemoveContainerAsync = name =>
            {
                _removedContainers.Add(name);
                return Task.CompletedTask;
            }
        };

    // A real program standing in for `docker run`: a batch file on Windows, a shell script elsewhere.
    private ProcessStartInfo Program(string windowsBody, string unixBody)
    {
        var path = Path.Combine(_dir.FullName, OperatingSystem.IsWindows() ? "fake-docker.cmd" : "fake-docker.sh");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, "@echo off\r\n" + windowsBody + "\r\n");
        }
        else
        {
            File.WriteAllText(path, "#!/bin/sh\n" + unixBody + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static ContainerRunExecutor.ContainerSecrets Secrets(
        KeyValuePair<string, string>[]? environment = null, KeyValuePair<string, string>[]? mounted = null) =>
        new([.. environment ?? []], [.. mounted ?? []], []);

    private string NewFile(string name)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private static readonly ToolchainResolution Resolution =
        new() { IsSuccess = true, Image = "img", Shell = "sh", Toolchain = "dotnet", Version = "10.0.100" };

    private Task<ExecutorResult> Run(
        ProcessStartInfo program, OutputLog log, string script, string? envFile = null, string? secretsFile = null,
        ContainerRunExecutor.ContainerSecrets? secrets = null, int timeoutSeconds = 60) =>
        NewExecutor().RunContainerAsync(
            program, Resolution, secrets ?? Secrets(), "prom-test", script, envFile, secretsFile,
            timeoutSeconds, log.Write, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AContainerThatPrintsAndSucceeds_StreamsStdoutAsInfo_AndStderrAsAWarningUnlessItStatesAnError()
    {
        var log = new OutputLog();
        var program = Program(
            "echo from-stdout\r\necho from-stderr 1>&2\r\necho ERROR from-error 1>&2",
            "echo from-stdout\necho from-stderr >&2\necho ERROR from-error >&2");

        var result = await Run(program, log, NewFile("step.sh"));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Null(result.FailureCode);
        Assert.True(log.Has("from-stdout", TaskLogLevel.Info));
        // R-36: standard error is a channel, not a verdict. An unmarked line is a Warning that keeps its channel
        // mark; only the line that states an error is an Error.
        Assert.True(log.Has("[stderr] from-stderr", TaskLogLevel.Warning));
        Assert.True(log.Has("ERROR from-error", TaskLogLevel.Error));
        Assert.False(log.Has("from-stderr", TaskLogLevel.Error));
    }

    [Fact]
    public async Task AContainerThatFails_ReturnsItsExitCode_WithoutAFailureCode()
    {
        var program = Program("exit /b 3", "exit 3");

        var result = await Run(program, new OutputLog(), NewFile("step.sh"));

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Null(result.FailureCode);
    }

    [Fact]
    public async Task AToolchainVersionMismatch_IsReportedAsAnInfrastructureMismatchNamingTheLockedVersion()
    {
        var code = ToolchainResolution.InfrastructureMismatchExitCode;
        var program = Program($"exit /b {code}", $"exit {code}");

        var result = await Run(program, new OutputLog(), NewFile("step.sh"));

        Assert.Equal(code, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(ToolchainResolution.InfrastructureMismatchCode, result.FailureCode);
        Assert.Equal("Toolchain 'dotnet' did not report the locked version '10.0.100'.", result.FailureReason);
    }

    [Fact]
    public async Task TheEnvFileAndTheSecretsFile_AreWrittenBeforeTheContainerStarts_AndRemovedAfterTheStep()
    {
        var script = NewFile("step.sh");
        var envFile = Path.Combine(_dir.FullName, "step.env");
        var secretsFile = Path.Combine(_dir.FullName, "step-secrets.sh");
        var program = Program(
            $"type \"{envFile}\"\r\ntype \"{secretsFile}\"",
            $"cat '{envFile}'\ncat '{secretsFile}'");
        var secrets = Secrets(
            environment: [new("PLAIN", "value")],
            mounted: [new("TLS_KEY", "line1\nit's")]);
        var log = new OutputLog();

        var result = await Run(program, log, script, envFile, secretsFile, secrets);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["PLAIN=value", "export TLS_KEY='line1", "it'\\''s'"],
            log.Lines.Select(l => l.Message).ToArray());
        Assert.False(File.Exists(script));
        Assert.False(File.Exists(envFile));
        Assert.False(File.Exists(secretsFile));
    }

    [Fact]
    public async Task AContainerThatOutlivesItsTimeout_IsRemovedByName_AndReportedAsTimedOut()
    {
        var script = NewFile("step.sh");
        var program = Program("ping -n 30 127.0.0.1 >nul", "sleep 30");

        var result = await Run(program, new OutputLog(), script, timeoutSeconds: 1);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(result.TimedOut);
        Assert.Equal(["prom-test"], _removedContainers);
        Assert.False(File.Exists(script));
    }
}
