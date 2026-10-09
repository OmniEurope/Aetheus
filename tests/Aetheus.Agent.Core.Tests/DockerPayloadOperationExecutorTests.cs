// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// BACK-012: the Docker operations whose user payload (Compose file, exec script, Dockerfile) used
/// to travel as a <c>printf | base64 -d | ...</c> shell string the command validator refused. The
/// argv is the boundary here, and the payload must reach docker as a file or as stdin, never as text
/// of a command line.
/// </summary>
public sealed class DockerPayloadOperationExecutorTests : IDisposable
{
    private readonly string _workDirectory = Directory.CreateTempSubdirectory("aetheus-docker-payload-tests-").FullName;

    public void Dispose() => Directory.Delete(_workDirectory, recursive: true);

    private DockerPayloadOperationExecutor NewExecutor() => new(
        Options.Create(new AetheusAgentOptions { WorkDirectory = _workDirectory }),
        NullLogger<DockerPayloadOperationExecutor>.Instance);

    [Fact]
    public void BuildArgs_AreExactAndCarryOnlyTheValidatedTarget()
    {
        Assert.Equal(
            new[] { "compose", "-f", Path.Combine("/work/web", "compose.yml"), "-p", "web", "up", "-d" },
            DockerPayloadOperationExecutor.BuildArgs(OperationKind.DockerComposeUp, "web", "/work/web"));
        Assert.Equal(
            new[] { "exec", "-i", "abc123def456", "sh" },
            DockerPayloadOperationExecutor.BuildArgs(OperationKind.DockerExecScript, "abc123def456", string.Empty));
        Assert.Equal(
            new[] { "build", "-t", "myapp:latest", "-f", Path.Combine("/tmp/b", "Dockerfile"), "/tmp/b" },
            DockerPayloadOperationExecutor.BuildArgs(OperationKind.DockerBuildImage, "myapp:latest", "/tmp/b"));
    }

    [Theory]
    [InlineData(OperationKind.DockerComposeUp, "web")]
    [InlineData(OperationKind.DockerExecScript, "abc123def456")]
    [InlineData(OperationKind.DockerBuildImage, "myapp:latest")]
    public void NewOperations_AreHandledAndTheirTargetsValidate(OperationKind kind, string target)
    {
        Assert.True(NewExecutor().CanHandle(kind));
        Assert.True(OperationTargetValidator.IsValid(kind, target));
    }

    [Theory]
    [InlineData(OperationKind.DockerComposeUp, "web; rm -rf /")]
    [InlineData(OperationKind.DockerExecScript, "not-a-container")]
    [InlineData(OperationKind.DockerBuildImage, "tag|pipe")]
    public async Task InvalidTarget_FailsBeforeAnyProcess(OperationKind kind, string target)
    {
        var lines = new List<string>();

        var result = await NewExecutor().ExecuteAsync(kind, target,
            new Dictionary<string, string> { [DockerPayloadOperationExecutor.PayloadVariable(kind)] = "Zm9v" },
            30, (line, _) => { lines.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(lines, line => line.Contains("Invalid docker target", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(OperationKind.DockerComposeUp, "web")]
    [InlineData(OperationKind.DockerExecScript, "abc123def456")]
    [InlineData(OperationKind.DockerBuildImage, "myapp:latest")]
    public async Task MissingPayload_FailsHonestly(OperationKind kind, string target)
    {
        var lines = new List<string>();

        var result = await NewExecutor().ExecuteAsync(kind, target, new Dictionary<string, string>(),
            30, (line, _) => { lines.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(lines, line => line.Contains(DockerPayloadOperationExecutor.PayloadVariable(kind), StringComparison.Ordinal));
    }

    [Fact]
    public async Task ComposeUp_WritesTheDecodedFileForTheStack_WhateverDockerAnswers()
    {
        // The payload carries every character the old shell form choked on.
        const string compose = "services: {}\n# $HOME | ; && `x`\n";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(compose));

        await NewExecutor().ExecuteAsync(OperationKind.DockerComposeUp, "aetheus-payload-test",
            new Dictionary<string, string> { [DockerPayloadOperationExecutor.ComposeVariable] = encoded },
            30, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        var written = await File.ReadAllTextAsync(
            Path.Combine(_workDirectory, "docker-compose", "aetheus-payload-test", "compose.yml"), TestContext.Current.CancellationToken);
        Assert.Equal(compose, written);
    }
}
