// SPDX-License-Identifier: EUPL-1.2
using System.IO.Compression;
using System.Security.Cryptography;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The steps of one job run concurrently (ADR-012), so the three <c>restore-artifacts</c> steps of
/// <c>publish-observability-packages</c> extract into the same workspace at the same time. Each archive
/// carries the same <c>.pipeline-artifacts</c> markers; extracting them in parallel used to fail with
/// "The process cannot access the file ... source-commit because it is being used by another process"
/// (production run 3353, R-17).
/// </summary>
public sealed class PipelineArtifactRestorerConcurrencyTests
{
    private const string Commit = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0";
    private const int Restores = 3;
    private const int Rounds = 60;
    private const int ConcurrentTransfers = 2;

    [Fact]
    public async Task ConcurrentRestoresOfArchivesSharingTheSameMarkers_AllSucceedIntoOneWorkspace()
    {
        var ct = TestContext.Current.CancellationToken;
        var failures = new List<string>();
        for (var round = 0; round < Rounds; round++)
        {
            var workDir = Directory.CreateTempSubdirectory("prm-concurrent-").FullName;
            try
            {
                var restores = Enumerable.Range(0, Restores).Select(NewRestore).ToArray();
                var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var arrived = 0;
                var results = await Task.WhenAll(restores.Select((restore, index) => Task.Run(async () =>
                {
                    var api = Substitute.For<IServerApiClient>();
                    api.DownloadArtifactAsync(100 + index, 42, Arg.Any<CancellationToken>())
                        .Returns(async _ =>
                        {
                            // The transfer budget lets two light restores run at once: those two extract together.
                            if (Interlocked.Increment(ref arrived) == ConcurrentTransfers) barrier.SetResult();
                            await barrier.Task;
                            return (Stream?)new MemoryStream(restore.Bytes);
                        });
                    var output = new List<string>();
                    var executor = new PipelineArtifactOperationExecutor(
                        api, NullLogger<PipelineArtifactOperationExecutor>.Instance, TimeProvider.System);
                    var result = await executor.ExecuteAsync(
                        OperationKind.PipelineRestoreArtifacts,
                        target: "Package-artifacts",
                        envVars: new Dictionary<string, string>
                        {
                            ["AETHEUS_RESTORE_ARTIFACT_ID"] = (100 + index).ToString(),
                            ["AETHEUS_RESTORE_RUN_ID"] = "42",
                            ["AETHEUS_RESTORE_ARTIFACT_SHA256"] = restore.Sha256,
                            ["AETHEUS_WORKING_DIR"] = workDir,
                            ["AETHEUS_RESTORE_EXPECTED_SOURCE_COMMIT"] = Commit
                        },
                        timeoutSeconds: 60,
                        onOutput: (line, _) =>
                        {
                            lock (output) output.Add(line);
                            return Task.CompletedTask;
                        },
                        cancellationToken: ct);
                    return (ExitCode: result.ExitCode, Output: output);
                }, ct)));

                foreach (var (exitCode, output) in results.Where(r => r.ExitCode != 0))
                    failures.Add($"round {round}: exit {exitCode}: "
                        + string.Join(" | ", output.Where(line => line.Contains("failed", StringComparison.Ordinal))));

                if (results.All(r => r.ExitCode == 0))
                {
                    Assert.Equal(Commit, (await File.ReadAllTextAsync(
                        Path.Combine(workDir, ".pipeline-artifacts", "source-commit"), ct)).Trim());
                    for (var index = 0; index < Restores; index++)
                        Assert.True(File.Exists(Path.Combine(workDir, $"payload-{index}.txt")), $"payload-{index}.txt");
                }
            }
            finally
            {
                Directory.Delete(workDir, recursive: true);
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} restore(s) failed. First: {failures.FirstOrDefault()}");
    }

    private static (byte[] Bytes, string Sha256) NewRestore(int index)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, $"payload-{index}.txt", $"package {index}");
            // The same markers, with the same content, in every archive.
            Write(archive, ".pipeline-artifacts/source-commit", Commit);
            Write(archive, ".pipeline-artifacts/delivery-contract.json",
                $$"""{"candidateVersion":"1.2.3","sourceSha":"{{Commit}}"}""");
            Write(archive, ".pipeline-artifacts/artifact-provenance.json", """{"builder":"aetheus-ci"}""");
        }
        var bytes = stream.ToArray();
        return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }
}
