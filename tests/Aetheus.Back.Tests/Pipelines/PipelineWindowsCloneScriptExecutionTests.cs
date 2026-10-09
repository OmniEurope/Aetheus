// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.Versioning;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Tests.Shared;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// BACK-017: runs the generated Windows prepare script in PowerShell against a fake <c>git</c> that
/// fails one chosen operation. PowerShell ignores native exit codes even under
/// <c>$ErrorActionPreference = 'Stop'</c>, so only a real execution proves the script stops.
/// </summary>
public sealed class PipelineWindowsCloneScriptExecutionTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aetheus-clone-" + Guid.NewGuid().ToString("N"));

    public PipelineWindowsCloneScriptExecutionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        // Fails the operation named by FAKE_GIT_FAIL (its first argument), succeeds otherwise.
        File.WriteAllText(Path.Combine(_root, "bin", "git.cmd"),
            "@echo off\r\nif \"%1\"==\"%FAKE_GIT_FAIL%\" exit /b 7\r\nexit /b 0\r\n");
    }

    [PlatformFact("windows")]
    [SupportedOSPlatform("windows")]
    public async Task PinnedPrepare_SucceedsWhenEveryGitCallSucceeds()
    {
        var (exitCode, output) = await RunAsync(fail: "none", sourceVersion: Sha, existingRepository: false);

        Assert.Equal(0, exitCode);
        Assert.Contains($"Repository pinned to {Sha}", output, StringComparison.Ordinal);
        Assert.Contains("Workspace ready", output, StringComparison.Ordinal);
    }

    [PlatformTheory("windows")]
    [InlineData("init", true, false)]
    [InlineData("remote", true, false)]
    [InlineData("fetch", true, false)]
    [InlineData("checkout", true, false)]
    [InlineData("reset", true, false)]
    [InlineData("fetch", false, true)]
    [InlineData("reset", false, true)]
    [InlineData("clone", false, false)]
    [SupportedOSPlatform("windows")]
    public async Task Prepare_FailsWhenAMandatoryGitCallFails(string fail, bool pinned, bool existingRepository)
    {
        var (exitCode, output) = await RunAsync(fail, pinned ? Sha : null, existingRepository);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("Workspace ready", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Repository pinned", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Repository cloned", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Repository updated", output, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Output)> RunAsync(
        string fail, string? sourceVersion, bool existingRepository)
    {
        var workspace = Path.Combine(_root, "ws");
        if (existingRepository) Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        var script = PipelineCommandBuilder.BuildWindowsCloneCommand(workspace);
        var startInfo = new ProcessStartInfo(ResolvePowerShell())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script })
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PATH"] = Path.Combine(_root, "bin") + Path.PathSeparator
            + Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment["FAKE_GIT_FAIL"] = fail;
        startInfo.Environment["REPOSITORY_URL"] = "https://example.invalid/repo.git";
        if (sourceVersion is null) startInfo.Environment.Remove("BUILD_SOURCEVERSION");
        else startInfo.Environment["BUILD_SOURCEVERSION"] = sourceVersion;

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }

    // Same preference as the agent's WindowsShellExecutor: pwsh when installed, Windows PowerShell otherwise.
    private static string ResolvePowerShell() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, "pwsh.exe")))
            ? "pwsh"
            : "powershell";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
