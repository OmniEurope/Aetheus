// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The resource monitor's reading of one <c>docker stats</c> sample: the exact argv sent to docker, and the
/// output kept only from a process that exits with code 0 (a real shell stands in for docker).
/// </summary>
[Collection(ProcessSpawningTestCollection.Name)]
public sealed class ScannerProcessOutputTests
{
    [Fact]
    public void TheDockerStatsReading_NamesTheContainerWithoutAShell()
    {
        var startInfo = ScannerProcessRunner.DockerStatsStartInfo("aetheus-scan-1");

        Assert.Equal("docker", startInfo.FileName);
        Assert.Equal(
            ["stats", "--no-stream", "--format", "{{.CPUPerc}}|{{.MemUsage}}|{{.BlockIO}}|{{.NetIO}}", "aetheus-scan-1"],
            startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public async Task AProcessThatSucceeds_GivesItsOutput()
    {
        var output = await ScannerProcessRunner.ReadOutputAsync(
            Shell("echo sample"), TestContext.Current.CancellationToken);

        Assert.Equal("sample", output?.Trim());
    }

    [Fact]
    public async Task AProcessThatFails_GivesNothing()
    {
        var output = await ScannerProcessRunner.ReadOutputAsync(
            Shell("echo partial && exit 3"), TestContext.Current.CancellationToken);

        Assert.Null(output);
    }

    private static ProcessStartInfo Shell(string command)
    {
        var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }
}
