// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Tests.Shared;

namespace Aetheus.Agent.Core.Tests;

public sealed class WindowsInstallerPathSafetyTests
{
    [PlatformFact("windows")]
    public async Task Validator_AcceptsSeparateApplicationAndDataDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aetheus-path-safety-{Guid.NewGuid():N}");

        var result = await RunAsync(Path.Combine(root, "install"), Path.Combine(root, "work"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PATHS_VALID", result.Output, StringComparison.Ordinal);
    }

    [PlatformTheory("windows")]
    [InlineData(@"C:\", @"C:\ProgramData\AetheusAgent")]
    [InlineData(@"C:\Program Files\AetheusAgent", @"C:\")]
    [InlineData(@"C:\Windows\AetheusAgent", @"C:\ProgramData\AetheusAgent")]
    [InlineData(@"C:\Program Files\AetheusAgent", @"C:\Program Files\AetheusAgent\work")]
    public async Task Validator_RejectsRootsSystemDirectoriesAndOverlaps(string installDir, string workDir)
    {
        var result = await RunAsync(installDir, workDir);

        Assert.NotEqual(0, result.ExitCode);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string installDir, string workDir)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = FindRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(FindRepoRoot(), "deploy", "agent-installer", "install-agent-windows.ps1"));
        startInfo.ArgumentList.Add("-ValidatePathsOnly");
        startInfo.ArgumentList.Add("-InstallDir");
        startInfo.ArgumentList.Add(installDir);
        startInfo.ArgumentList.Add("-WorkDir");
        startInfo.ArgumentList.Add(workDir);

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string FindRepoRoot() => Aetheus.Agent.Core.Tests.RepositoryScan.Root;
}
