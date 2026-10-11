// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Runtime.Versioning;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Tests.Shared;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// BACK-017: the Linux workspace cleanup runs for real in bash. It used to print "Workspace cleaned
/// successfully" whatever <c>rm -rf</c> did; it now retries, then fails with what is left over.
/// </summary>
[SupportedOSPlatform("linux")]
public class PipelineWorkspaceCleanupScriptTests
{
    [PlatformFact("linux")]
    public async Task RemovableWorkspace_IsCleaned_AndSucceeds()
    {
        var root = Directory.CreateTempSubdirectory("cleanup-ok-").FullName;
        var workspace = Path.Combine(root, "s");
        Directory.CreateDirectory(Path.Combine(workspace, "nested"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "nested", "file.txt"), "x", TestContext.Current.CancellationToken);

        var (exit, output) = await RunAsync(PipelineCommandBuilder.BuildLinuxCleanupCommand(workspace), pathPrefix: null);

        Assert.Equal(0, exit);
        Assert.Contains("Workspace cleaned successfully", output);
        Assert.False(Directory.Exists(workspace));
        Directory.Delete(root, recursive: true);
    }

    [PlatformFact("linux")]
    public async Task WorkspaceThatStays_FailsAfterThreeAttempts_WithoutClaimingSuccess()
    {
        var root = Directory.CreateTempSubdirectory("cleanup-ko-").FullName;
        var workspace = Path.Combine(root, "s");
        Directory.CreateDirectory(workspace);
        // An rm that removes nothing stands for a file still held by a process: works as root too.
        var shims = Path.Combine(root, "bin");
        Directory.CreateDirectory(shims);
        var fakeRm = Path.Combine(shims, "rm");
        await File.WriteAllTextAsync(fakeRm, "#!/bin/sh\nexit 1\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(fakeRm, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var (exit, output) = await RunAsync(PipelineCommandBuilder.BuildLinuxCleanupCommand(workspace), shims);

        Assert.Equal(1, exit);
        Assert.Equal(3, output.Split("Workspace still present, retrying").Length - 1);
        Assert.Contains("Workspace cleanup failed after 3 attempts", output);
        Assert.DoesNotContain("Workspace cleaned successfully", output);
        Directory.Delete(root, recursive: true);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string script, string? pathPrefix)
    {
        var start = new ProcessStartInfo("bash", ["-c", script])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (pathPrefix is not null)
            start.Environment["PATH"] = $"{pathPrefix}:{Environment.GetEnvironmentVariable("PATH")}";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }
}
