// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>PLAN-010 lot 1 proof on a real Docker daemon: the collector, through the real shell runner,
/// reports a throwaway privileged container and a throwaway container holding the Docker socket. Explicit:
/// it needs Docker and creates containers, so it runs only when asked (<c>-explicit only</c>). The two
/// containers are removed by their exact names, before and after.</summary>
public sealed class DockerContainerSecurityRealDockerTests
{
    private const string PrivilegedName = "aetheus-plan010-priv-probe";
    private const string SocketName = "aetheus-plan010-sock-probe";
    private const string Image = "alpine:3.20";

    [Fact(Explicit = true)]
    public async Task RealDocker_ReportsPrivilegedAndSocketMountedThrowawayContainers()
    {
        var ct = TestContext.Current.CancellationToken;
        var shell = new DefaultShellRunner(NullLogger<DefaultShellRunner>.Instance);
        await RemoveAsync(shell, ct);
        try
        {
            await RunAsync(shell, ["run", "-d", "--name", PrivilegedName, "--privileged", Image, "sleep", "300"], ct);
            await RunAsync(shell,
                ["run", "-d", "--name", SocketName, "-v", "/var/run/docker.sock:/var/run/docker.sock", Image, "sleep", "300"], ct);

            var docker = await new DockerCollector(NullLogger<DockerCollector>.Instance, shell).CollectAllAsync(ct);

            var privileged = Assert.Single(docker.Containers, c => c.Name == PrivilegedName);
            Assert.True(privileged.SecurityInspected);
            Assert.Equal(DockerContainerRisks.Privileged, privileged.Risks);
            var socket = Assert.Single(docker.Containers, c => c.Name == SocketName);
            Assert.True(socket.SecurityInspected);
            Assert.Equal(DockerContainerRisks.DockerSocket, socket.Risks);
            TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, docker.Containers
                .Select(c => $"{c.Name}\tinspected={c.SecurityInspected}\trisks={c.Risks}\tcaps={c.DangerousCapabilities}\trole={c.AetheusRole}")));
        }
        finally
        {
            await RemoveAsync(shell, CancellationToken.None);
        }
    }

    private static async Task RunAsync(IShellRunner shell, IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await shell.RunExecAsync("docker", args, ct);
        Assert.True(result.ExitCode == 0, $"docker {string.Join(' ', args)} failed: {result.StdErr}");
    }

    private static Task RemoveAsync(IShellRunner shell, CancellationToken ct) =>
        shell.RunExecAsync("docker", ["rm", "-f", PrivilegedName, SocketName], ct);
}
