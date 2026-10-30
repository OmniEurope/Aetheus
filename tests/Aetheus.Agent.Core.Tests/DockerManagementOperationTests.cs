// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>R-119: the Docker page's changes that were plain shell strings (container and image removal, resource
/// limits, compose stack actions, prune) are typed operations: a fixed docker argv where only validated values vary,
/// claimed only by an agent that publishes <see cref="AgentCapabilities.DockerManagement"/>.</summary>
public sealed class DockerManagementOperationTests
{
    private static DockerOperationExecutor Executor() =>
        new(Options.Create(new AetheusAgentOptions()), NullLogger<DockerOperationExecutor>.Instance);

    private static readonly OperationKind[] ManagementKinds =
    [
        OperationKind.DockerRemoveContainer, OperationKind.DockerRemoveImage, OperationKind.DockerUpdateResources,
        OperationKind.DockerComposeProjectUp, OperationKind.DockerComposeProjectDown,
        OperationKind.DockerComposeProjectRestart, OperationKind.DockerPrune
    ];

    [Fact]
    public void EveryDockerKind_IsHandledByADockerExecutor_AndNeedsADockerCapability()
    {
        var options = Options.Create(new AetheusAgentOptions());
        IOperationExecutor[] executors =
        [
            Executor(),
            new DockerPayloadOperationExecutor(options, NullLogger<DockerPayloadOperationExecutor>.Instance)
        ];
        var dockerKinds = Enum.GetValues<OperationKind>()
            .Where(kind => kind.ToString().StartsWith("Docker", StringComparison.Ordinal))
            .ToList();

        Assert.Superset(ManagementKinds.ToHashSet(), dockerKinds.ToHashSet());
        Assert.All(dockerKinds, kind => Assert.Single(executors, executor => executor.CanHandle(kind)));
        Assert.All(ManagementKinds, kind => Assert.Equal(AgentCapabilities.DockerManagement, AgentCapabilities.RequiredFor(kind)));
        Assert.Equal(AgentCapabilities.DockerExecution, AgentCapabilities.RequiredFor(OperationKind.DockerStopContainer));
    }

    [Fact]
    public void DockerManagement_IsASoftwareCapability_SoAnOlderAgentIsToldToUpdate() =>
        Assert.Contains(AgentCapabilities.DockerManagement, AgentCapabilities.SoftwareCapabilities);

    [Theory]
    [InlineData(OperationKind.DockerComposeProjectUp, new[] { "compose", "-p", "web", "up", "-d" })]
    [InlineData(OperationKind.DockerComposeProjectDown, new[] { "compose", "-p", "web", "down" })]
    [InlineData(OperationKind.DockerComposeProjectRestart, new[] { "compose", "-p", "web", "restart" })]
    public void ComposeProject_Argv_IsFixed_WithTheStackAsProjectName(OperationKind kind, string[] expected) =>
        Assert.Equal(expected, DockerOperationExecutor.BuildArgs(kind, "web"));

    [Fact]
    public void UpdateResources_Argv_CarriesTheValidatedLimits()
    {
        var env = new Dictionary<string, string> { ["AETHEUS_DOCKER_CPUS"] = "1.50", ["AETHEUS_DOCKER_MEMORY_MB"] = "512" };

        Assert.Equal(
            ["update", "--cpus=1.50", "--memory=512m", "abc123def456"],
            DockerOperationExecutor.BuildUpdateArgs("abc123def456", env)!);
        Assert.Equal(
            ["update", "--memory=64m", "abc123def456"],
            DockerOperationExecutor.BuildUpdateArgs("abc123def456", new Dictionary<string, string> { ["AETHEUS_DOCKER_MEMORY_MB"] = "64" })!);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("1 --privileged", null)]
    [InlineData("1,5", null)]
    [InlineData(null, "0")]
    [InlineData(null, "12m")]
    [InlineData(null, "99999999")]
    [InlineData("2", "--memory-swap=1")]
    public void UpdateResources_RefusesMissingOrMalformedLimits(string? cpus, string? memory)
    {
        var env = new Dictionary<string, string>();
        if (cpus is not null) env["AETHEUS_DOCKER_CPUS"] = cpus;
        if (memory is not null) env["AETHEUS_DOCKER_MEMORY_MB"] = memory;

        Assert.Null(DockerOperationExecutor.BuildUpdateArgs("abc123def456", env)!);
    }

    [Fact]
    public async Task UpdateResources_WithoutLimits_FailsWithoutRunningDocker()
    {
        var output = new List<(string Text, TaskLogLevel Level)>();

        var result = await Executor().ExecuteAsync(
            OperationKind.DockerUpdateResources, "abc123def456", new Dictionary<string, string>(), 30,
            (text, level) => { output.Add((text, level)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal([("Invalid docker resource limits", TaskLogLevel.Error)], output);
    }

    [Theory]
    [InlineData("containers", new[] { "container prune -f" })]
    [InlineData("images,volumes", new[] { "image prune -af", "volume prune -f" })]
    [InlineData("containers,images,volumes", new[] { "container prune -f", "image prune -af", "volume prune -f" })]
    public void Prune_RunsOneFixedCommandPerTarget_InOrder(string target, string[] expected) =>
        Assert.Equal(expected, DockerOperationExecutor.BuildPruneArgs(target)!.Select(args => string.Join(' ', args)));

    [Theory]
    [InlineData(OperationKind.DockerRemoveContainer, "web; rm -rf /")]
    [InlineData(OperationKind.DockerRemoveImage, "--force")]
    [InlineData(OperationKind.DockerUpdateResources, "not-a-container-id")]
    [InlineData(OperationKind.DockerComposeProjectDown, "Web.Stack")]
    [InlineData(OperationKind.DockerComposeProjectUp, "web -f /etc/passwd")]
    [InlineData(OperationKind.DockerPrune, "system")]
    [InlineData(OperationKind.DockerPrune, "containers,containers")]
    [InlineData(OperationKind.DockerPrune, "images,containers")]
    public async Task InvalidTarget_IsRefusedWithoutRunningDocker(OperationKind kind, string target)
    {
        var output = new List<(string Text, TaskLogLevel Level)>();

        var result = await Executor().ExecuteAsync(
            kind, target, new Dictionary<string, string> { ["AETHEUS_DOCKER_CPUS"] = "1" }, 30,
            (text, level) => { output.Add((text, level)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal([("Invalid docker target format", TaskLogLevel.Error)], output);
    }

    [Theory]
    [InlineData(OperationKind.DockerRemoveImage, "sha256:abcdef1234560000000000000000000000000000000000000000000000000000")]
    [InlineData(OperationKind.DockerRemoveImage, "abc123def456")]
    [InlineData(OperationKind.DockerRemoveContainer, "abc123def456")]
    [InlineData(OperationKind.DockerUpdateResources, "abc123def456")]
    [InlineData(OperationKind.DockerComposeProjectRestart, "my-stack")]
    [InlineData(OperationKind.DockerPrune, "containers,images,volumes")]
    public void ValidTarget_IsAccepted_ByTheSharedValidator(OperationKind kind, string target) =>
        Assert.True(OperationTargetValidator.IsValid(kind, target));
}
