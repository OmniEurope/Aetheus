// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Toolchains;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>After a container step, its script, environment file and secrets file are deleted, and a file
/// that cannot be deleted never prevents deleting the others nor fails the step.</summary>
public sealed class ContainerRunExecutorCleanupTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("aetheus-container-cleanup-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static ContainerRunExecutor NewExecutor() =>
        new(
            Options.Create(new AetheusAgentOptions()),
            NullLogger<ContainerRunExecutor>.Instance,
            new ToolchainResolver(),
            TimeProvider.System);

    private string NewFile(string name)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public async Task TheScriptTheEnvFileAndTheSecretsFile_AreAllDeleted()
    {
        var script = NewFile("step.sh");
        var env = NewFile("step.env");
        var secrets = NewFile("step-secrets.sh");

        await NewExecutor().CleanupRunFilesAsync("c1", script, env, secrets, CancellationToken.None);

        Assert.False(File.Exists(script));
        Assert.False(File.Exists(env));
        Assert.False(File.Exists(secrets));
    }

    [Fact]
    public async Task AStepWithoutEnvOrSecretsFile_DeletesOnlyItsScript_AndLeavesOtherFilesAlone()
    {
        var script = NewFile("step.sh");
        var unrelated = NewFile("unrelated.txt");

        await NewExecutor().CleanupRunFilesAsync("c1", script, envFilePath: null, secretsFilePath: null, CancellationToken.None);

        Assert.False(File.Exists(script));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task AFileThatCannotBeDeleted_DoesNotStopTheOthersFromBeingDeleted()
    {
        var script = NewFile("step.sh");
        var env = NewFile("step.env");
        var secrets = NewFile("step-secrets.sh");
        // On Windows an open file with no sharing cannot be deleted; on Linux the delete simply succeeds.
        using var lockedScript = new FileStream(script, FileMode.Open, FileAccess.Read, FileShare.None);

        await NewExecutor().CleanupRunFilesAsync("c1", script, env, secrets, CancellationToken.None);

        Assert.False(File.Exists(env));
        Assert.False(File.Exists(secrets));
        if (OperatingSystem.IsWindows())
            Assert.True(File.Exists(script));
    }
}
