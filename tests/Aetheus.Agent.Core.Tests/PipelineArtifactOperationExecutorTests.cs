// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Covers S-TECH-61: artifact collection auto-detects Cobertura coverage and publishes it so
/// shell-based runs light up the coverage tile without an explicit <c>type: coverage</c> step.
/// </summary>
[Collection(ProcessSpawningTestCollection.Name)]
public class PipelineArtifactOperationExecutorTests
{
    [Theory]
    [InlineData(null, CompressionLevel.Fastest)]
    [InlineData("fastest", CompressionLevel.Fastest)]
    [InlineData("Optimal", CompressionLevel.Optimal)]
    [InlineData("NoCompression", CompressionLevel.NoCompression)]
    public void CollectArtifacts_CompressionMode_IsExplicitAndValidated(
        string? value,
        CompressionLevel expected)
    {
        Assert.Equal(expected, PipelineArtifactCollector.ResolveCompressionLevel(value));
    }

    [Fact]
    public void CollectArtifacts_UnknownCompressionMode_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            PipelineArtifactCollector.ResolveCompressionLevel("maximum-magic"));
    }

    [Theory]
    [InlineData("report.JSON", CompressionLevel.Optimal)]
    [InlineData("contract.yaml", CompressionLevel.Optimal)]
    [InlineData("package.ZIP", CompressionLevel.NoCompression)]
    [InlineData("image.tar", CompressionLevel.Fastest)]
    [InlineData("runtime.dll", CompressionLevel.Fastest)]
    [InlineData("no-extension", CompressionLevel.Fastest)]
    public void CollectArtifacts_AdaptiveCompression_UsesFileCategory(
        string filePath,
        CompressionLevel expected)
    {
        Assert.Equal(expected, PipelineArtifactCollector.ResolveCompressionLevel("Adaptive", filePath));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(268435456, 1)]
    [InlineData(268435457, 2)]
    [InlineData(1224736768, 5)]
    public void CollectArtifacts_CompletedUploadPartCount_MatchesChunkBoundaries(long bytes, long expected)
    {
        Assert.Equal(expected, PipelineArtifactCollector.GetUploadPartCount(bytes));
    }

    [Theory]
    [InlineData("nminus1", "nminus1")]
    [InlineData("versions/v1", "versions/v1")]
    public void RestoreArtifacts_TargetDirectory_StaysInsideWorkspace(string target, string expectedSuffix)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "aetheus-restore-root");

        var resolved = PipelineArtifactRestorer.ResolveRestoreDirectory(workspace, target);

        Assert.Equal(Path.GetFullPath(Path.Combine(workspace, expectedSuffix)), resolved);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/../../escape")]
    public void RestoreArtifacts_TargetDirectoryTraversal_IsRejected(string target)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "aetheus-restore-root");

        Assert.Throws<InvalidDataException>(() =>
            PipelineArtifactRestorer.ResolveRestoreDirectory(workspace, target));
    }

    private const string CoberturaXml =
        "<?xml version=\"1.0\"?><coverage line-rate=\"0.85\" branch-rate=\"0.7\" " +
        "lines-covered=\"85\" lines-valid=\"100\" branches-covered=\"7\" branches-valid=\"10\"></coverage>";

    private static PipelineArtifactOperationExecutor NewExecutor(IServerApiClient apiClient)
    {
        apiClient.UploadArtifactAsync(
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<Stream>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var stream = call.ArgAt<Stream>(3);
                var start = stream.Position;
                var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
                stream.Position = start;
                return new PipelineArtifactDto
                {
                    Id = 101,
                    SizeBytes = stream.Length - start,
                    Sha256 = digest
                };
            });
        apiClient.PublishAnalysisReportAsync(Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new AnalysisReportDto { GateStatus = AnalysisGateStatus.Passed });
        return new PipelineArtifactOperationExecutor(
            apiClient, NullLogger<PipelineArtifactOperationExecutor>.Instance, TimeProvider.System);
    }

    private static string Sha256(Stream stream)
    {
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        return hash;
    }

    [Fact]
    public async Task RestoreArtifacts_DownloadsAndExtractsVerifiedArchiveIntoRequestedSubdirectory()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-restore-").FullName;
        try
        {
            var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("src/Aetheus.Back/bin/Release/net10.0/Aetheus.Back.dll");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("verified-build");
            }
            bytes.Position = 0;
            var api = Substitute.For<IServerApiClient>();
            api.DownloadArtifactAsync(91, 42, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Stream?>(bytes));

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelineRestoreArtifacts,
                target: "BuildArtifacts-artifacts",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RESTORE_ARTIFACT_ID"] = "91",
                    ["AETHEUS_RESTORE_RUN_ID"] = "42",
                    ["AETHEUS_RESTORE_ARTIFACT_SHA256"] = Sha256(bytes),
                    ["AETHEUS_WORKING_DIR"] = workDir,
                    ["AETHEUS_RESTORE_TARGET_DIR"] = ".nminus1"
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("verified-build", await File.ReadAllTextAsync(
                Path.Combine(workDir, ".nminus1", "src", "Aetheus.Back", "bin", "Release", "net10.0", "Aetheus.Back.dll"),
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(workDir,
                "src", "Aetheus.Back", "bin", "Release", "net10.0", "Aetheus.Back.dll")));
            await api.Received(1).DownloadArtifactAsync(91, 42, Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("previous-deployed")]
    [InlineData("current-deployed")]
    public async Task RestoreArtifacts_DeployedSelector_ExportsVerifiedBaselineIdentity(string selector)
    {
        var workDir = Directory.CreateTempSubdirectory("prm-baseline-").FullName;
        try
        {
            const string sourceSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string contractJson =
                """{"candidateVersion":"1.0.42","sourceSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""";
            var contractBytes = System.Text.Encoding.UTF8.GetBytes(contractJson);
            var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry(".pipeline-artifacts/delivery-contract.json");
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(contractBytes, TestContext.Current.CancellationToken);
            }
            bytes.Position = 0;
            var api = Substitute.For<IServerApiClient>();
            api.DownloadArtifactAsync(91, 42, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Stream?>(bytes));
            var output = new List<string>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelineRestoreArtifacts,
                target: "BuildArtifacts-artifacts",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RESTORE_ARTIFACT_ID"] = "91",
                    ["AETHEUS_RESTORE_RUN_ID"] = "42",
                    ["AETHEUS_RESTORE_ARTIFACT_SHA256"] = Sha256(bytes),
                    ["AETHEUS_WORKING_DIR"] = workDir,
                    ["AETHEUS_RESTORE_RELEASE_SELECTOR"] = selector
                },
                timeoutSeconds: 60,
                onOutput: (message, _) => { output.Add(message); return Task.CompletedTask; },
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("##aetheus[setvariable name=DELIVERY_BASELINE_BOOTSTRAP]false", output);
            Assert.Contains("##aetheus[setvariable name=DELIVERY_BASELINE_CANDIDATE_VERSION]1.0.42", output);
            Assert.Contains($"##aetheus[setvariable name=DELIVERY_BASELINE_SOURCE_SHA]{sourceSha}", output);
            Assert.Contains(
                $"##aetheus[setvariable name=DELIVERY_BASELINE_CONTRACT_SHA256]{Convert.ToHexString(SHA256.HashData(contractBytes)).ToLowerInvariant()}",
                output);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("previous-deployed")]
    [InlineData("current-deployed")]
    public async Task RestoreArtifacts_DeployedSelectorWithoutContract_FailsClosed(string selector)
    {
        var workDir = Directory.CreateTempSubdirectory("prm-baseline-missing-").FullName;
        try
        {
            var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("payload.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("payload");
            }
            bytes.Position = 0;
            var api = Substitute.For<IServerApiClient>();
            api.DownloadArtifactAsync(91, 42, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Stream?>(bytes));
            var output = new List<string>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelineRestoreArtifacts,
                target: "BuildArtifacts-artifacts",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RESTORE_ARTIFACT_ID"] = "91",
                    ["AETHEUS_RESTORE_RUN_ID"] = "42",
                    ["AETHEUS_RESTORE_ARTIFACT_SHA256"] = Sha256(bytes),
                    ["AETHEUS_WORKING_DIR"] = workDir,
                    ["AETHEUS_RESTORE_RELEASE_SELECTOR"] = selector
                },
                timeoutSeconds: 60,
                onOutput: (message, _) => { output.Add(message); return Task.CompletedTask; },
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(output, message =>
                message.Contains("does not contain .pipeline-artifacts/delivery-contract.json", StringComparison.Ordinal));
            Assert.DoesNotContain(output, message => message.Contains("DELIVERY_BASELINE_BOOTSTRAP", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreArtifacts_WithoutRequiredMetadata_FailsHonestly()
    {
        var api = Substitute.For<IServerApiClient>();

        var result = await NewExecutor(api).ExecuteAsync(
            OperationKind.PipelineRestoreArtifacts,
            target: "BuildArtifacts-artifacts",
            envVars: new Dictionary<string, string>(),
            timeoutSeconds: 60,
            onOutput: (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        await api.DidNotReceive().DownloadArtifactAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreArtifacts_Sha256Mismatch_IsRefusedBeforeExtraction()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-sha-").FullName;
        try
        {
            var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("payload.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("tampered");
            }
            bytes.Position = 0;
            var api = Substitute.For<IServerApiClient>();
            api.DownloadArtifactAsync(91, 42, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Stream?>(bytes));
            var output = new List<string>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelineRestoreArtifacts,
                "BuildArtifacts-artifacts",
                new Dictionary<string, string>
                {
                    ["AETHEUS_RESTORE_ARTIFACT_ID"] = "91",
                    ["AETHEUS_RESTORE_RUN_ID"] = "42",
                    ["AETHEUS_RESTORE_ARTIFACT_SHA256"] = new string('0', 64),
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                60,
                (message, _) => { output.Add(message); return Task.CompletedTask; },
                TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(output, message => message.StartsWith("Artifact SHA-256 mismatch:", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(workDir, "payload.txt")));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateRelease_UsesAuthoritativeRunMetadataAndCiArtifactRun()
    {
        const string commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var api = Substitute.For<IServerApiClient>();
        api.CreateReleaseAsync(5, 99, "1.0.99", null, commit, "main", 42, true, Arg.Any<CancellationToken>())
            .Returns(new ReleaseCreatedResponse { Id = 7, Version = "1.0.99", BuildNumber = 3 });

        var result = await NewExecutor(api).ExecuteAsync(
            OperationKind.PipelineCreateRelease,
            target: "1.0.99",
            envVars: new Dictionary<string, string>
            {
                ["AETHEUS_PROJECT_ID"] = "5",
                ["AETHEUS_RUN_ID"] = "99",
                ["AETHEUS_CHANGELOG"] = "false",
                ["AETHEUS_RELEASE_COMMIT"] = commit,
                ["AETHEUS_RELEASE_BRANCH"] = "main",
                ["AETHEUS_RELEASE_ARTIFACT_RUN_ID"] = "42",
                ["AETHEUS_RELEASE_DEPLOYED"] = "true"
            },
            timeoutSeconds: 60,
            onOutput: (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        await api.Received(1).CreateReleaseAsync(
            5, 99, "1.0.99", null, commit, "main", 42, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRelease_NullServerResponse_FailsHonestly()
    {
        var api = Substitute.For<IServerApiClient>();
        api.CreateReleaseAsync(5, 99, "1.0.99", null, null, null, null, false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReleaseCreatedResponse?>(null));
        var output = new List<string>();

        var result = await NewExecutor(api).ExecuteAsync(
            OperationKind.PipelineCreateRelease,
            target: "1.0.99",
            envVars: new Dictionary<string, string>
            {
                ["AETHEUS_PROJECT_ID"] = "5",
                ["AETHEUS_RUN_ID"] = "99",
                ["AETHEUS_CHANGELOG"] = "false"
            },
            timeoutSeconds: 60,
            onOutput: (message, _) => { output.Add(message); return Task.CompletedTask; },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(output, message => message.Contains("server returned no release", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreArtifacts_MissingDownload_FailsHonestly()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-restore-").FullName;
        try
        {
            var api = Substitute.For<IServerApiClient>();
            api.DownloadArtifactAsync(91, 42, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Stream?>(null));
            var output = new List<string>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelineRestoreArtifacts, "BuildArtifacts-artifacts",
                new Dictionary<string, string>
                {
                    ["AETHEUS_RESTORE_ARTIFACT_ID"] = "91",
                    ["AETHEUS_RESTORE_RUN_ID"] = "42",
                    ["AETHEUS_RESTORE_ARTIFACT_SHA256"] = new string('0', 64),
                    ["AETHEUS_WORKING_DIR"] = workDir
                }, 60, (message, _) => { output.Add(message); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(output, message => message.Contains("not found / forbidden", StringComparison.Ordinal));
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public async Task RestoreArtifacts_InvalidZip_FailsHonestly()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-restore-").FullName;
        try
        {
            var api = Substitute.For<IServerApiClient>();
            var invalidZip = new MemoryStream("not a zip"u8.ToArray());
            api.DownloadArtifactAsync(91, 42, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Stream?>(invalidZip));
            var output = new List<string>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelineRestoreArtifacts, "BuildArtifacts-artifacts",
                new Dictionary<string, string>
                {
                    ["AETHEUS_RESTORE_ARTIFACT_ID"] = "91",
                    ["AETHEUS_RESTORE_RUN_ID"] = "42",
                    ["AETHEUS_RESTORE_ARTIFACT_SHA256"] = Sha256(invalidZip),
                    ["AETHEUS_WORKING_DIR"] = workDir
                }, 60, (message, _) => { output.Add(message); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(output, message => message.StartsWith("Artifact restore failed:", StringComparison.Ordinal));
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public async Task RestoreArtifacts_InsufficientFreeSpace_IsRefusedBeforeExtraction()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-space-").FullName;
        try
        {
            var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("must-not-extract.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("payload");
            }
            bytes.Position = 0;
            var api = Substitute.For<IServerApiClient>();
            api.DownloadArtifactAsync(91, 42, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Stream?>(bytes));
            var output = new List<string>();

            var result = await PipelineArtifactRestorer.RestoreAsync(
                api, NullLogger.Instance,
                new Dictionary<string, string>
                {
                    ["AETHEUS_RESTORE_ARTIFACT_ID"] = "91",
                    ["AETHEUS_RESTORE_RUN_ID"] = "42",
                    ["AETHEUS_RESTORE_ARTIFACT_SHA256"] = Sha256(bytes),
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                (message, _) => { output.Add(message); return Task.CompletedTask; },
                TestContext.Current.CancellationToken,
                _ => 512L * 1024 * 1024);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(output, message => message.StartsWith("Artifact restore refused:", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(workDir, "must-not-extract.txt")));
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public async Task CollectArtifacts_WithCoberturaInWorkspace_AutoPublishesCoverage()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-cov-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "coverage.cobertura.xml"), CoberturaXml, TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelineCollectArtifacts,
                target: "[]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_STAGE_NAME"] = "Tests",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            await api.Received(1).PublishCoverageAsync(42, CoberturaXml, "Tests",
                Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task CollectArtifacts_WithoutCoverage_DoesNotPublish()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-cov-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "app.dll"), "binary", TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();
            var output = new List<string>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelineCollectArtifacts,
                target: "[]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (message, _) => { output.Add(message); return Task.CompletedTask; },
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(output, message =>
                message.StartsWith("Artifact uploaded successfully: 1 part(s), sha256 ", StringComparison.Ordinal));
            await api.DidNotReceive().PublishCoverageAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task CollectArtifacts_BackendDigestMismatch_FailsWithoutSuccessLog()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-artifact-digest-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "payload.txt"), "payload",
                TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();
            var executor = NewExecutor(api);
            api.UploadArtifactAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
                    Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(call => new PipelineArtifactDto
                {
                    Id = 101,
                    SizeBytes = call.ArgAt<Stream>(3).Length,
                    Sha256 = new string('0', 64)
                });
            var output = new List<string>();

            var result = await executor.ExecuteAsync(
                OperationKind.PipelineCollectArtifacts,
                target: "[]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (message, _) => { output.Add(message); return Task.CompletedTask; },
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(output, message => message.Contains("size or SHA-256 does not match", StringComparison.Ordinal));
            Assert.DoesNotContain(output, message =>
                message.StartsWith("Artifact uploaded successfully", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    public async Task CollectArtifacts_EmptyWorkspaceWithoutPatterns_FailsWithoutUpload(string target)
    {
        var workDir = Directory.CreateTempSubdirectory("prm-artifact-").FullName;
        try
        {
            var api = Substitute.For<IServerApiClient>();
            var output = new List<string>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelineCollectArtifacts,
                target: target,
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (message, _) => { output.Add(message); return Task.CompletedTask; },
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(output, message => message.Contains("holds no file to collect", StringComparison.Ordinal));
            await api.DidNotReceive().UploadArtifactAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task CollectArtifacts_ExplicitPatternsWithoutMatches_FailsWithoutUpload()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-artifact-").FullName;
        try
        {
            var api = Substitute.For<IServerApiClient>();
            var output = new List<string>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelineCollectArtifacts,
                target: "[\"bin/**/*.zip\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (message, _) => { output.Add(message); return Task.CompletedTask; },
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(output, message => message.Contains("no files matched", StringComparison.Ordinal));
            await api.DidNotReceive().UploadArtifactAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishCoverage_WithNestedCoberturaFile_FindsAndPublishesIt()
    {
        // 0-d: the dotnet `--collect` writes to coverage/<guid>/coverage.cobertura.xml - the default
        // `**/coverage.cobertura.xml` glob must match that nested path, not only the workspace root.
        var workDir = Directory.CreateTempSubdirectory("prm-cov-").FullName;
        try
        {
            var nested = Path.Combine(workDir, "coverage", Guid.NewGuid().ToString());
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(nested, "coverage.cobertura.xml"), CoberturaXml, TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishCoverage,
                target: "[\"**/coverage.cobertura.xml\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_STAGE_NAME"] = "Coverage",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            await api.Received(1).PublishCoverageAsync(42, CoberturaXml, "Coverage",
                Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishCoverage_WithExactNestedRelativePath_FindsAndPublishesIt()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-cov-exact-").FullName;
        try
        {
            var nested = Path.Combine(workDir, "coverage-merged");
            Directory.CreateDirectory(nested);
            await File.WriteAllTextAsync(Path.Combine(nested, "Cobertura.xml"), CoberturaXml, TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishCoverage,
                target: "[\"coverage-merged/Cobertura.xml\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_STAGE_NAME"] = "Coverage",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            await api.Received(1).PublishCoverageAsync(42, CoberturaXml, "Coverage",
                Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishCoverage_NoFilesFound_FailsInsteadOfFalseGreen()
    {
        // 0-d / no-fake regression: a coverage step that finds nothing must NOT report success.
        var workDir = Directory.CreateTempSubdirectory("prm-cov-").FullName;
        try
        {
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishCoverage,
                target: "[\"**/coverage.cobertura.xml\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await api.DidNotReceive().PublishCoverageAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishCoverage_MultipleRawReports_RequiresExplicitMerge()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-cov-multi-").FullName;
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(workDir, "coverage", "one")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(workDir, "coverage", "two")).FullName;
            await File.WriteAllTextAsync(Path.Combine(first, "coverage.cobertura.xml"), CoberturaXml, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(second, "coverage.cobertura.xml"), CoberturaXml, TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();
            var output = new List<string>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishCoverage,
                target: "[\"**/coverage.cobertura.xml\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (message, _) => { output.Add(message); return Task.CompletedTask; },
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(output, message => message.Contains("one merged Cobertura report", StringComparison.Ordinal));
            await api.DidNotReceive().PublishCoverageAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public async Task PublishCoverage_InvalidLineTotals_FailsBeforePublishing()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-cov-invalid-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "Cobertura.xml"),
                "<coverage line-rate=\"1\" lines-covered=\"2\" lines-valid=\"1\" />",
                TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishCoverage,
                target: "[\"Cobertura.xml\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await api.DidNotReceive().PublishCoverageAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally { Directory.Delete(workDir, recursive: true); }
    }

    [Fact]
    public async Task PublishCoverage_SourceCommitMismatch_FailsBeforePublishing()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-cov-sha-").FullName;
        try
        {
            await RunGitAsync(workDir, "init");
            await RunGitAsync(workDir, "-c", "user.name=Aetheus Tests", "-c", "user.email=aetheus@example.invalid",
                "commit", "--allow-empty", "-m", "fixture");
            await File.WriteAllTextAsync(Path.Combine(workDir, "Cobertura.xml"), CoberturaXml, TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishCoverage,
                target: "[\"Cobertura.xml\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir,
                    ["BUILD_SOURCEVERSION"] = new string('a', 40)
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await api.DidNotReceive().PublishCoverageAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally { DeleteGitFixture(workDir); }
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        // WHY: pre-push hook env inheritance incidents - any repository-local git variable (GIT_DIR,
        // GIT_COMMON_DIR, ...) would redirect this fixture git call to the real repository.
        GitRepositoryEnvironment.Neutralize(startInfo.Environment);

        using var process = Process.Start(startInfo)!;
        var standardError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {standardError}");
    }

    private static void DeleteGitFixture(string workingDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(workingDirectory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(workingDirectory, recursive: true);
    }

    [Fact]
    public async Task PublishCoverage_BelowThreshold_PublishesButFailsStep()
    {
        // S-FEAT-K3P8: coverage is published (trend recorded) but the step fails when below the floor.
        var workDir = Directory.CreateTempSubdirectory("prm-cov-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "coverage.cobertura.xml"), CoberturaXml, TestContext.Current.CancellationToken); // 85%
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishCoverage,
                target: "[\"**/coverage.cobertura.xml\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir,
                    ["AETHEUS_MIN_COVERAGE"] = "90"
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await api.Received(1).PublishCoverageAsync(42, CoberturaXml, Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishCoverage_MeetsThreshold_Succeeds()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-cov-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "coverage.cobertura.xml"), CoberturaXml, TestContext.Current.CancellationToken); // 85%
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishCoverage,
                target: "[\"**/coverage.cobertura.xml\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir,
                    ["AETHEUS_MIN_COVERAGE"] = "80"
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishCoverage_WhenImmutableArtifactIsNotReturned_FailsClosed()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-cov-artifact-").FullName;
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workDir, "coverage.cobertura.xml"),
                CoberturaXml,
                TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();
            var executor = NewExecutor(api);
            api.UploadArtifactAsync(
                    Arg.Any<int>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<Stream>(),
                    Arg.Any<CancellationToken>())
                .Returns((PipelineArtifactDto?)null);

            var result = await executor.ExecuteAsync(
                OperationKind.PipelinePublishCoverage,
                target: "[\"**/coverage.cobertura.xml\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await api.DidNotReceive().PublishAnalysisReportAsync(
                Arg.Any<int>(),
                Arg.Any<PublishAnalysisReportRequest>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    private const string HighComplexitySource =
        "class C { int M(int x) { if (x > 0) return 1; if (x > 1) return 2; if (x > 2) return 3; " +
        "if (x > 3) return 4; return 0; } }";

    [Fact]
    public async Task PublishComplexity_OverBudget_PublishesButFailsStep()
    {
        // S-FEAT-D7M5: metrics are published (trend recorded) but the step fails when max CC > budget.
        var workDir = Directory.CreateTempSubdirectory("prm-cc-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "Sample.cs"), HighComplexitySource, TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishComplexity,
                target: string.Empty,
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir,
                    ["AETHEUS_MAX_COMPLEXITY"] = "2"
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await api.Received(1).PublishComplexityAsync(42, Arg.Any<double>(), Arg.Any<int>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishComplexity_WithinBudget_Succeeds()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-cc-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "Sample.cs"), HighComplexitySource, TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishComplexity,
                target: string.Empty,
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir,
                    ["AETHEUS_MAX_COMPLEXITY"] = "100"
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    /// <summary>
    /// PLAN-003 2.4: a lint step with analysis_category: accessibility publishes its SARIF as
    /// Accessibility analysis, and keeps it out of the code-quality lint store.
    /// </summary>
    [Fact]
    public async Task PublishLint_AccessibilityCategory_PublishesAccessibilityAnalysisOnly()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-lint-a11y-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "accessibility.sarif"), "{}", TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishLint,
                target: "[\"**/accessibility.sarif\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir,
                    [LintAnalysisCategories.VariableName] = "Accessibility"
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            await api.Received(1).PublishAnalysisReportAsync(
                42, Arg.Is<PublishAnalysisReportRequest>(request => request.Category == AnalysisCategory.Accessibility),
                Arg.Any<CancellationToken>());
            await api.DidNotReceive().PublishLintAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishLint_UnknownCategory_FailsBeforePublishingAnything()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-lint-bad-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "x.sarif"), "{}", TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishLint,
                target: "[\"**/*.sarif\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir,
                    [LintAnalysisCategories.VariableName] = "Sast"
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await api.DidNotReceive().PublishAnalysisReportAsync(
                Arg.Any<int>(), Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishLint_NoFilesFound_FailsInsteadOfFalseGreen()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-lint-").FullName;
        try
        {
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishLint,
                target: "[\"**/*.sarif\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await api.DidNotReceive().PublishLintAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishLint_MultipleOverlappingPatterns_PublishesEveryDistinctFileOnce()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-lint-many-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, "nested"));
            await File.WriteAllTextAsync(
                Path.Combine(workDir, "first.sarif"),
                "{}",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(workDir, "nested", "second.sarif"),
                "{}",
                TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishLint,
                target: "[\"**/*.sarif\",\"first.sarif\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            await api.Received(2).PublishLintAsync(
                42, "{}", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await api.Received(2).PublishAnalysisReportAsync(
                42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task PublishLint_WhenSecondPublicationFails_FailsTheStep()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-lint-failure-").FullName;
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(workDir, "first.sarif"),
                "{}",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(workDir, "second.sarif"),
                "{}",
                TestContext.Current.CancellationToken);
            var api = Substitute.For<IServerApiClient>();
            var calls = 0;
            api.When(candidate => candidate.PublishLintAsync(
                    Arg.Any<int>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<string?>(),
                    Arg.Any<CancellationToken>()))
                .Do(_ =>
                {
                    if (Interlocked.Increment(ref calls) == 2)
                        throw new HttpRequestException("second publication failed");
                });

            var result = await NewExecutor(api).ExecuteAsync(
                OperationKind.PipelinePublishLint,
                target: "[\"**/*.sarif\"]",
                envVars: new Dictionary<string, string>
                {
                    ["AETHEUS_RUN_ID"] = "42",
                    ["AETHEUS_WORKING_DIR"] = workDir
                },
                timeoutSeconds: 60,
                onOutput: (_, _) => Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.NotEqual(0, result.ExitCode);
            await api.Received(2).PublishLintAsync(
                42, "{}", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
            await api.Received(1).PublishAnalysisReportAsync(
                42, Arg.Any<PublishAnalysisReportRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ── D-04: the restore proves which build it restored ──────────────────────────────────────────
    //
    // The archive SHA proves the bytes arrived intact; it says nothing about WHICH build they are.
    // Every consumer used to rewrite that comparison in shell afterwards, so a consumer that forgot
    // the stage silently scanned, graded or shipped another commit's build. These pin that the
    // restore itself now refuses, and that it still allows the one case where another commit is the
    // whole point.

    private const string RunCommit = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0";

    /// <summary>An artifact laid out the way the pipelines actually build one: payload plus the
    /// three provenance files under .pipeline-artifacts.</summary>
    private static async Task<MemoryStream> ProvenancedArtifactAsync(
        string? sourceCommit = RunCommit,
        bool withContract = true,
        bool withManifest = true)
    {
        var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            var payload = archive.CreateEntry("src/Aetheus.Back/bin/Release/net10.0/Aetheus.Back.dll");
            await using (var writer = new StreamWriter(payload.Open()))
                await writer.WriteAsync("verified-build");

            if (sourceCommit is not null)
            {
                var commit = archive.CreateEntry(".pipeline-artifacts/source-commit");
                await using var writer = new StreamWriter(commit.Open());
                await writer.WriteAsync(sourceCommit);
            }
            if (withContract)
            {
                var contract = archive.CreateEntry(".pipeline-artifacts/delivery-contract.json");
                await using var writer = new StreamWriter(contract.Open());
                await writer.WriteAsync($$"""{"candidateVersion":"1.2.3","sourceSha":"{{RunCommit}}"}""");
            }
            if (withManifest)
            {
                var manifest = archive.CreateEntry(".pipeline-artifacts/artifact-provenance.json");
                await using var writer = new StreamWriter(manifest.Open());
                await writer.WriteAsync("""{"builder":"aetheus-ci"}""");
            }
        }
        bytes.Position = 0;
        return bytes;
    }

    private async Task<Aetheus.Agent.Core.Executors.ExecutorResult> RestoreWithProvenanceAsync(
        MemoryStream artifact,
        string workDir,
        string? expectedCommit,
        string? releaseSelector = null,
        List<string>? log = null)
    {
        var api = Substitute.For<IServerApiClient>();
        api.DownloadArtifactAsync(91, 42, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(artifact));

        var envVars = new Dictionary<string, string>
        {
            ["AETHEUS_RESTORE_ARTIFACT_ID"] = "91",
            ["AETHEUS_RESTORE_RUN_ID"] = "42",
            ["AETHEUS_RESTORE_ARTIFACT_SHA256"] = Sha256(artifact),
            ["AETHEUS_WORKING_DIR"] = workDir
        };
        if (expectedCommit is not null)
            envVars["AETHEUS_RESTORE_EXPECTED_SOURCE_COMMIT"] = expectedCommit;
        if (releaseSelector is not null)
            envVars["AETHEUS_RESTORE_RELEASE_SELECTOR"] = releaseSelector;

        return await NewExecutor(api).ExecuteAsync(
            OperationKind.PipelineRestoreArtifacts,
            target: "BuildArtifacts-artifacts",
            envVars: envVars,
            timeoutSeconds: 60,
            onOutput: (line, _) =>
            {
                log?.Add(line);
                return Task.CompletedTask;
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RestoreArtifacts_ArtifactBuiltFromTheRunsRevision_IsAccepted()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-prov-ok-").FullName;
        try
        {
            var result = await RestoreWithProvenanceAsync(
                await ProvenancedArtifactAsync(), workDir, RunCommit);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreArtifacts_ArtifactBuiltFromAnotherRevision_IsRefused()
    {
        // The defect D-04 exists to stop: scanning or shipping a build of a revision nobody looked at.
        var workDir = Directory.CreateTempSubdirectory("prm-prov-other-").FullName;
        var log = new List<string>();
        try
        {
            var result = await RestoreWithProvenanceAsync(
                await ProvenancedArtifactAsync(sourceCommit: new string('b', 40)),
                workDir, RunCommit, log: log);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains(log, line => line.Contains("provenance refused", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreArtifacts_ArtifactWithoutASourceCommit_IsRefused()
    {
        var workDir = Directory.CreateTempSubdirectory("prm-prov-none-").FullName;
        var log = new List<string>();
        try
        {
            var result = await RestoreWithProvenanceAsync(
                await ProvenancedArtifactAsync(sourceCommit: null), workDir, RunCommit, log: log);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains(log, line => line.Contains("no", StringComparison.Ordinal)
                && line.Contains("source-commit", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreArtifacts_ArtifactWithoutProvenance_IsRefusedEvenWhenTheWorkspaceHoldsValidMarkers()
    {
        // Markers left by an earlier restore must not vouch for an archive that carries none.
        var workDir = Directory.CreateTempSubdirectory("prm-prov-inherited-").FullName;
        var log = new List<string>();
        try
        {
            var metadata = Directory.CreateDirectory(Path.Combine(workDir, ".pipeline-artifacts")).FullName;
            await File.WriteAllTextAsync(Path.Combine(metadata, "source-commit"), RunCommit, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(metadata, "delivery-contract.json"), "{}", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(metadata, "artifact-provenance.json"), "{}", TestContext.Current.CancellationToken);

            var result = await RestoreWithProvenanceAsync(
                await ProvenancedArtifactAsync(sourceCommit: null, withContract: false, withManifest: false),
                workDir, RunCommit, log: log);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains(log, line => line.Contains("provenance refused", StringComparison.Ordinal));
            // Refused before extraction: the payload never reaches the workspace.
            Assert.False(File.Exists(Path.Combine(workDir, "src", "Aetheus.Back", "bin", "Release", "net10.0", "Aetheus.Back.dll")));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RestoreArtifacts_MissingContractOrProvenanceManifest_IsRefused(
        bool withContract, bool withManifest)
    {
        var workDir = Directory.CreateTempSubdirectory("prm-prov-partial-").FullName;
        var log = new List<string>();
        try
        {
            var result = await RestoreWithProvenanceAsync(
                await ProvenancedArtifactAsync(withContract: withContract, withManifest: withManifest),
                workDir, RunCommit, log: log);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains(log, line => line.Contains("provenance refused", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreArtifacts_WithoutAnExpectedCommit_SkipsTheProvenanceCheck()
    {
        // A run that does not declare its revision has nothing to compare against; refusing here
        // would break every consumer rather than protect it.
        var workDir = Directory.CreateTempSubdirectory("prm-prov-skip-").FullName;
        try
        {
            var result = await RestoreWithProvenanceAsync(
                await ProvenancedArtifactAsync(sourceCommit: null), workDir, expectedCommit: null);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreArtifacts_SourceCommitComparisonIgnoresCaseAndTrailingNewline()
    {
        // git and the shell that wrote the file disagree about both; neither is a provenance failure.
        var workDir = Directory.CreateTempSubdirectory("prm-prov-trim-").FullName;
        try
        {
            var result = await RestoreWithProvenanceAsync(
                await ProvenancedArtifactAsync(sourceCommit: RunCommit.ToUpperInvariant() + "\r\n"),
                workDir, RunCommit);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
