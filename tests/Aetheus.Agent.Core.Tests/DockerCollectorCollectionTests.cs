// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Behaviour coverage for the collector orchestration. The pure parsing edge cases live in
/// <see cref="DockerCollectorTests"/>; these tests deliberately exercise the five Docker queries,
/// stats enrichment and honest degradation when Docker is unavailable.
/// </summary>
public sealed class DockerCollectorCollectionTests
{
    [Fact]
    public async Task CollectAllAsync_MapsEveryDockerResourceAndEnrichesContainerStats()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("docker", Arg.Is<IReadOnlyList<string>>(a => a.Contains("ps") && !a.Contains("stats")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0,
                "abc123\tapi_web_1\tapi:latest\trunning\tUp 5 minutes\t0.0.0.0:8080->80/tcp\t2026-07-14 09:00:00 +0000 UTC\t\n" +
                "ignored\ttoo-short\n", string.Empty));
        shell.RunExecAsync("docker", Arg.Is<IReadOnlyList<string>>(a => a.Contains("stats")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "abc123\t12.5%\t256MiB / 2GiB\ninvalid\trow\n", string.Empty));
        shell.RunExecAsync("docker", Arg.Is<IReadOnlyList<string>>(a => a.Contains("images")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0,
                "sha256:1\tapi\tlatest\t120MB\t2026-07-14 08:00:00 +0000 UTC\nshort\trow\n", string.Empty));
        shell.RunExecAsync("docker", Arg.Is<IReadOnlyList<string>>(a => a.Contains("compose")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0,
                "NAME STATUS CONFIG FILES\napi running(2) C:/deploy/docker compose.yml\nbroken\n", string.Empty));
        shell.RunExecAsync("docker", Arg.Is<IReadOnlyList<string>>(a => a.Contains("network")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "net1\tapi_default\tbridge\tlocal\tapi\nshort\n", string.Empty));
        shell.RunExecAsync("docker", Arg.Is<IReadOnlyList<string>>(a => a.Contains("volume")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "api_data\tlocal\t/var/lib/docker/volumes/api_data\tapi\nshort\n", string.Empty));
        shell.RunExecAsync("docker", Arg.Is<IReadOnlyList<string>>(a => a.Contains("inspect") && a.Contains("abc123")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0,
                """{"id":"abc123def456","name":"/api_web_1","image":"api:latest","privileged":true,"pidMode":"","networkMode":"bridge","capAdd":null,"mounts":[],"role":null,"service":null}""" + "\n",
                string.Empty));

        var result = await new DockerCollector(NullLogger<DockerCollector>.Instance, shell).CollectAllAsync(TestContext.Current.CancellationToken);

        var container = Assert.Single(result.Containers);
        Assert.Equal("abc123", container.ContainerId);
        Assert.True(container.SecurityInspected);
        Assert.Equal(DockerContainerRisks.Privileged, container.Risks);
        Assert.Equal("api", container.Project);
        Assert.Equal(12.5, container.CpuPercent);
        Assert.Equal(256, container.MemoryUsageMb);
        Assert.Equal(2048, container.MemoryLimitMb);
        Assert.Equal("api", Assert.Single(result.Images).Repository);
        var stack = Assert.Single(result.ComposeStacks);
        Assert.Equal(2, stack.RunningCount);
        Assert.Equal(2, stack.TotalCount);
        Assert.Equal("C:/deploy/docker compose.yml", stack.ConfigFile);
        Assert.Equal("api", Assert.Single(result.Networks).Project);
        Assert.Equal("api", Assert.Single(result.Volumes).Project);
        await shell.Received(7).RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CollectAllAsync_DockerUnavailable_ReturnsEmptySnapshotInsteadOfThrowing()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<ShellExecResult>>(_ => throw new InvalidOperationException("docker unavailable"));

        var result = await new DockerCollector(NullLogger<DockerCollector>.Instance, shell).CollectAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Containers);
        Assert.Empty(result.Images);
        Assert.Empty(result.ComposeStacks);
        Assert.Empty(result.Networks);
        Assert.Empty(result.Volumes);
    }
}
