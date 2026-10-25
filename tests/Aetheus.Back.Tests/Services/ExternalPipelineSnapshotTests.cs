// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Tests.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests;

public sealed class ExternalPipelineSnapshotTests
{
    [Fact]
    public async Task PipelineRead_UsesPinnedCommit_AndReturnsNullOnlyWhenAbsent()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("pipeline-snapshot-test-").FullName;
        var runner = new GitProcessRunner(NullLogger<GitProcessRunner>.Instance);
        var sut = new GitCliService(NullLogger<GitCliService>.Instance, runner);
        try
        {
            async Task<string> Git(params string[] args)
            {
                var result = await runner.RunGitAsync(directory, args, ct);
                Assert.True(result.ExitCode == 0, result.Error);
                return result.Output.Trim();
            }
            await Git("init");
            GitFixtureGuard.AssertOwnedBy(directory, directory);
            await Git("config", "user.name", "Test");
            await Git("config", "user.email", "test@aetheus.invalid");
            Directory.CreateDirectory(Path.Combine(directory, ".pipeline"));
            var file = Path.Combine(directory, ".pipeline", "package-ci.yaml");
            const string yaml = "name: Package CI\nstages:\n  - name: Build\n    steps:\n      - name: Compile\n        shell: dotnet build\n";
            await File.WriteAllTextAsync(file, yaml, ct);
            await Git("add", ".pipeline");
            await Git("-c", "core.hooksPath=", "commit", "-m", "first");
            var commit = await Git("rev-parse", "HEAD");
            await File.WriteAllTextAsync(file, yaml.Replace("dotnet build", "dotnet test", StringComparison.Ordinal), ct);
            await Git("add", ".pipeline");
            await Git("-c", "core.hooksPath=", "commit", "-m", "second");
            Assert.Equal(yaml, await sut.ReadPipelineYamlFromSnapshotAsync(directory, commit, "Package CI", ct));
            Assert.Null(await sut.ReadPipelineYamlFromSnapshotAsync(directory, commit, "Missing", ct));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
        }
    }
}
