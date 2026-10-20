// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>A shell step is refused before any process starts when the command validator rejects the
/// command (with its reason, or "Unknown" when it names none) or when the environment carries a
/// dangerous variable name; the refusal is reported on the step log as an error and ends with -1.</summary>
[Collection(ProcessSpawningTestCollection.Name)]
public sealed class ShellExecutorBaseValidationTests
{
    private readonly ICommandValidator _validator = Substitute.For<ICommandValidator>();
    private readonly List<(string Line, TaskLogLevel Level)> _output = [];

    private sealed class RecordingShellExecutor(
        ICommandValidator validator, ExecutorProcessRunner runner)
        : ShellExecutorBase(validator, Microsoft.Extensions.Options.Options.Create(new AetheusAgentOptions()), runner, NullLogger.Instance)
    {
        public bool ProcessBuilt { get; private set; }

        protected override ProcessStartInfo BuildProcessStartInfo(string command)
        {
            ProcessBuilt = true;
            // A shell that only exits 0: `dotnet --version` depended on the host resolving an SDK from
            // the test's working directory, and exited 155 on the CI agent whose SDK is pinned elsewhere.
            var info = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true }
                : new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in OperatingSystem.IsWindows() ? new[] { "/c", "exit 0" } : new[] { "-c", "exit 0" })
                info.ArgumentList.Add(argument);
            return info;
        }

        protected override void LogExecution(int timeoutSeconds)
        {
        }
    }

    private RecordingShellExecutor Executor() =>
        new(_validator, new ExecutorProcessRunner(new AgentRuntimeHealth(TimeProvider.System), NullLogger<ExecutorProcessRunner>.Instance));

    private Task Record(string line, TaskLogLevel level)
    {
        _output.Add((line, level));
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("chmod 777 /", "chmod is not allowed", "Command blocked by security policy - chmod is not allowed")]
    [InlineData("whatever", null, "Command blocked by security policy - Unknown")]
    public async Task ARejectedCommand_IsRefusedWithItsReason_AndNoProcessIsBuilt(string command, string? reason, string expectedLine)
    {
        _validator.IsAllowed(command).Returns(false);
        _validator.GetRejectionReason(command).Returns(reason);
        var executor = Executor();

        var result = await executor.ExecuteAsync(command, [], 30, Record, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal([(expectedLine, TaskLogLevel.Error)], _output);
        Assert.False(executor.ProcessBuilt);
    }

    [Fact]
    public async Task ADangerousEnvironmentVariable_IsRefusedByName_AndNoProcessIsBuilt()
    {
        var environment = new Dictionary<string, string> { ["LD_PRELOAD"] = "/tmp/x.so", ["PATH_X"] = "1" };
        _validator.IsAllowed("echo hi").Returns(true);
        _validator.GetDangerousEnvironmentVariableNames(environment).Returns(["LD_PRELOAD", "BASH_ENV"]);
        var executor = Executor();

        var result = await executor.ExecuteAsync("echo hi", environment, 30, Record, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        var line = Assert.Single(_output);
        Assert.Equal(TaskLogLevel.Error, line.Level);
        Assert.StartsWith("Blocked dangerous environment variable(s): LD_PRELOAD, BASH_ENV.", line.Line, StringComparison.Ordinal);
        Assert.False(executor.ProcessBuilt);
    }

    [Fact]
    public async Task AnAllowedCommandWithACleanEnvironment_ReachesTheProcess()
    {
        var environment = new Dictionary<string, string> { ["SAFE"] = "1" };
        _validator.IsAllowed("echo hi").Returns(true);
        _validator.GetDangerousEnvironmentVariableNames(environment).Returns([]);
        var executor = Executor();

        var result = await executor.ExecuteAsync("echo hi", environment, 30, Record, TestContext.Current.CancellationToken);

        Assert.True(executor.ProcessBuilt);
        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(_output, entry => entry.Level == TaskLogLevel.Error && entry.Line.StartsWith("Command blocked", StringComparison.Ordinal));
    }
}
