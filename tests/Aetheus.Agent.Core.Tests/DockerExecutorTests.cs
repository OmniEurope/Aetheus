// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public class DockerExecutorTests
{
    private readonly ILogger<DockerExecutor> _loggerMock = Substitute.For<ILogger<DockerExecutor>>();
    private readonly ICommandValidator _validatorMock = Substitute.For<ICommandValidator>();
    private readonly DockerExecutor _sut;

    public DockerExecutorTests()
    {
        _validatorMock.IsAllowed(Arg.Any<string>()).Returns(true);
        _validatorMock.GetDangerousEnvironmentVariableNames(Arg.Any<Dictionary<string, string>>())
            .Returns([]);
        var options = Options.Create(new AetheusAgentOptions());
        var processRunner = new ExecutorProcessRunner(
            new AgentRuntimeHealth(TimeProvider.System),
            NullLogger<ExecutorProcessRunner>.Instance);
        _sut = new DockerExecutor(_validatorMock, options, processRunner, _loggerMock);
    }

    [Fact]
    public void Type_ReturnsDocker()
    {
        Assert.Equal(ExecutorType.Docker, _sut.Type);
    }

    [Fact]
    public async Task ExecuteAsync_NoSpace_ReturnsError()
    {
        var outputMessages = new List<string>();

        var result = await _sut.ExecuteAsync(
            "invalid-no-space",
            new Dictionary<string, string>(),
            30,
            (msg, level) => { outputMessages.Add(msg); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains(outputMessages, m => m.Contains("Invalid docker exec command"));
    }

    [Fact]
    public void TryParseExecCommand_WithSpace_ParsesContainerAndCommand()
    {
        // Assert the real parsing contract on the extracted pure method, without depending on a docker
        // binary being present/absent (the old test spawned a process and only checked a Debug log call).
        var ok = DockerExecutor.TryParseExecCommand("test-container echo hello", out var container, out var inner, out var error);

        Assert.True(ok);
        Assert.Equal("test-container", container);
        Assert.Equal("echo hello", inner);
        Assert.Empty(error);
    }

    [Fact]
    public void TryParseExecCommand_NoSpace_ReturnsError()
    {
        var ok = DockerExecutor.TryParseExecCommand("invalid-no-space", out var container, out _, out var error);

        Assert.False(ok);
        Assert.Empty(container); // empty id distinguishes the missing-space case from a malformed id
        Assert.Contains("missing container ID", error);
    }

    [Fact]
    public void TryParseExecCommand_MalformedContainerId_ReturnsError()
    {
        var ok = DockerExecutor.TryParseExecCommand("bad;id echo hi", out var container, out _, out var error);

        Assert.False(ok);
        Assert.Equal("bad;id", container); // split succeeded, but the id fails the format check
        Assert.Contains("Invalid container ID format", error);
    }

    [Fact]
    public void BuildDockerExec_ContainsNoEnvironmentValueOrCommandInArgv()
    {
        const string secret = "top-secret-value";
        const string command = "printf done";

        var startInfo = DockerExecutor.BuildDockerExec("container-1");
        var arguments = startInfo.ArgumentList.ToArray();

        Assert.Equal(["exec", "-i", "container-1", "/bin/sh"], arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument.Contains(command, StringComparison.Ordinal));
        Assert.True(startInfo.RedirectStandardInput);
    }

    [Fact]
    public void BuildStandardInputScript_QuotesSecretsAndPreservesCommand()
    {
        var script = DockerExecutor.BuildStandardInputScript(
            new Dictionary<string, string>
            {
                ["TOKEN"] = "line 1\nline '2'",
                ["EMPTY"] = string.Empty
            },
            "printf done");

        Assert.Equal(
            "export TOKEN='line 1\nline '\\''2'\\'''\nexport EMPTY=''\nprintf done\n",
            script);
    }

    [Theory]
    [InlineData("VALID_NAME_42", true)]
    [InlineData("_VALID", true)]
    [InlineData("9INVALID", false)]
    [InlineData("INVALID-NAME", false)]
    [InlineData("INVALID\nNAME", false)]
    public void IsValidShellKey_RequiresPosixIdentifier(string key, bool expected) =>
        Assert.Equal(expected, DockerExecutor.IsValidShellKey(key));

    private async Task<(ExecutorResult Result, List<(string Message, TaskLogLevel Level)> Output)> ExecuteAsync(
        DockerExecutor executor, string command, Dictionary<string, string> environment)
    {
        var output = new List<(string Message, TaskLogLevel Level)>();
        var result = await executor.ExecuteAsync(
            command, environment, 30,
            (message, level) => { lock (output) output.Add((message, level)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);
        return (result, output);
    }

    [Fact]
    public async Task ExecuteAsync_MalformedContainerId_IsRefusedAndLogged()
    {
        var (result, output) = await ExecuteAsync(_sut, "bad;id echo hi", []);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(("Invalid container ID format", TaskLogLevel.Error), Assert.Single(output));
        _loggerMock.ReceivedWithAnyArgs(1).Log(LogLevel.Warning, default, default(object)!, default, default!);
    }

    [Fact]
    public async Task ExecuteAsync_CommandRefusedByTheValidator_IsBlockedWithItsReason()
    {
        _validatorMock.IsAllowed("rm -rf /").Returns(false);
        _validatorMock.GetRejectionReason("rm -rf /").Returns("destructive command");

        var (result, output) = await ExecuteAsync(_sut, "web-1 rm -rf /", []);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(("Command blocked by security policy - destructive command", TaskLogLevel.Error), Assert.Single(output));
    }

    [Fact]
    public async Task ExecuteAsync_CommandRefusedWithoutAReason_SaysUnknown()
    {
        _validatorMock.IsAllowed("odd").Returns(false);
        _validatorMock.GetRejectionReason("odd").Returns((string?)null);

        var (_, output) = await ExecuteAsync(_sut, "web-1 odd", []);

        Assert.Equal("Command blocked by security policy - Unknown", Assert.Single(output).Message);
    }

    [Fact]
    public async Task ExecuteAsync_DangerousEnvironmentVariables_AreBlockedByName()
    {
        var environment = new Dictionary<string, string> { ["LD_PRELOAD"] = "/x.so", ["PATH"] = "/tmp" };
        _validatorMock.GetDangerousEnvironmentVariableNames(environment).Returns(["LD_PRELOAD", "PATH"]);

        var (result, output) = await ExecuteAsync(_sut, "web-1 echo hi", environment);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(
            ("Blocked dangerous environment variable(s): LD_PRELOAD, PATH. Rename application settings to a scoped name such as DEPLOY_ENV.", TaskLogLevel.Error),
            Assert.Single(output));
    }

    [Fact]
    public async Task ExecuteAsync_EnvironmentNamesThatAreNotShellIdentifiers_AreBlocked()
    {
        var environment = new Dictionary<string, string> { ["GOOD"] = "1", ["BAD-NAME"] = "2", ["9X"] = "3" };

        var (result, output) = await ExecuteAsync(_sut, "web-1 echo hi", environment);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(("Blocked invalid environment variable name(s): BAD-NAME, 9X.", TaskLogLevel.Error), Assert.Single(output));
    }

    [Fact]
    public async Task ExecuteAsync_ValidCommand_ExecsTheContainerShell_FeedsTheScriptOnStandardInput_AndReturnsItsExitCode()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aetheus-docker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executor = new DockerExecutor(
                _validatorMock,
                Options.Create(new AetheusAgentOptions()),
                new ExecutorProcessRunner(new AgentRuntimeHealth(TimeProvider.System), NullLogger<ExecutorProcessRunner>.Instance),
                _loggerMock)
            {
                DockerProgram = StandInProgram.Write(directory, "docker",
                    "echo ARGS %*\r\nfindstr \"^\"\r\nexit /b 3",
                    "echo ARGS \"$@\"\ncat\nexit 3"),
            };

            var (result, output) = await ExecuteAsync(
                executor, "web-1 printf done", new Dictionary<string, string> { ["TOKEN"] = "top secret" });

            Assert.Equal(3, result.ExitCode);
            Assert.False(result.TimedOut);
            var lines = output.Where(line => line.Level == TaskLogLevel.Info).Select(line => line.Message.TrimEnd()).ToList();
            // The secret travels on standard input only, never in the argument list (the runner then
            // appends its own resource-usage lines).
            Assert.Equal(["ARGS exec -i web-1 /bin/sh", "export TOKEN='top secret'", "printf done"], lines.Take(3));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
