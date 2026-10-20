// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The Docker operation is refused before any process is spawned when its target is not a
/// plain container or image identifier, and a kind the executor does not own runs nothing.</summary>
public sealed class DockerOperationExecutorTests
{
    private static DockerOperationExecutor Executor() =>
        new(Options.Create(new AetheusAgentOptions()), NullLogger<DockerOperationExecutor>.Instance);

    [Theory]
    [InlineData(OperationKind.DockerStartContainer, "web; rm -rf /")]
    [InlineData(OperationKind.DockerStopContainer, "$(id)")]
    [InlineData(OperationKind.DockerRestartContainer, "web container")]
    [InlineData(OperationKind.DockerPullImage, "--privileged")]
    public async Task InvalidTarget_IsRefusedWithoutRunningDocker(OperationKind kind, string target)
    {
        var output = new List<(string Text, TaskLogLevel Level)>();

        var result = await Executor().ExecuteAsync(
            kind, target, 30, (text, level) => { output.Add((text, level)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal([("Invalid docker target format", TaskLogLevel.Error)], output);
    }

    [Fact]
    public async Task KindOutsideTheDockerOperations_RunsNothing()
    {
        var output = new List<string>();

        var result = await Executor().ExecuteAsync(
            OperationKind.BlueGreenRetire, "aetheus-prod", 30, (text, _) => { output.Add(text); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(output);
    }
}
