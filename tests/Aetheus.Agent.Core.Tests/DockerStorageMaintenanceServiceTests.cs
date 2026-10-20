// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Extensions;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public sealed class DockerStorageMaintenanceServiceTests
{
    [Fact]
    public void Options_EnableBoundedMutationByDefault()
    {
        var options = new DockerStorageMaintenanceOptions();

        Assert.True(options.Enabled);
        Assert.False(options.DryRun);
        Assert.False(options.AllowBuildsOnDeploymentTarget);
        Assert.Equal(DockerStorageMaintenanceOptions.CurrentPolicyVersion, options.PolicyVersion);
        Assert.Equal(5, options.ReservedSpaceGiB);
        Assert.Equal(15, options.MaxCacheGiB);
        Assert.True(options.NuGetCacheRetentionDays > 0);
    }

    [Fact]
    public void AddAgentCore_PreviousStoragePolicy_IsCappedWithoutRewritingInstalledConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aetheus:ServerUrl"] = "http://localhost:5301",
                ["Aetheus:DockerStorageMaintenance:PolicyVersion"] = "2",
                ["Aetheus:DockerStorageMaintenance:ReservedSpaceGiB"] = "20",
                ["Aetheus:DockerStorageMaintenance:MaxCacheGiB"] = "80"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentCore(configuration);
        using var provider = services.BuildServiceProvider();

        var storage = provider.GetRequiredService<IOptions<AetheusAgentOptions>>()
            .Value.DockerStorageMaintenance;

        Assert.Equal(DockerStorageMaintenanceOptions.CurrentPolicyVersion, storage.PolicyVersion);
        Assert.Equal(5, storage.ReservedSpaceGiB);
        Assert.Equal(15, storage.MaxCacheGiB);
    }

    [Fact]
    public void AddAgentCore_CurrentStoragePolicy_CannotExceedRuntimeCacheCap()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aetheus:ServerUrl"] = "http://localhost:5301",
                ["Aetheus:DockerStorageMaintenance:PolicyVersion"] = "3",
                ["Aetheus:DockerStorageMaintenance:ReservedSpaceGiB"] = "20",
                ["Aetheus:DockerStorageMaintenance:MaxCacheGiB"] = "80"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentCore(configuration);
        using var provider = services.BuildServiceProvider();

        var storage = provider.GetRequiredService<IOptions<AetheusAgentOptions>>()
            .Value.DockerStorageMaintenance;

        Assert.Equal(15, storage.ReservedSpaceGiB);
        Assert.Equal(15, storage.MaxCacheGiB);
    }

    [Fact]
    public async Task MeasureManagedPathSize_LimitReturnsPartialBytesInsteadOfZero()
    {
        var root = Path.Combine(Path.GetTempPath(), $"storage-measure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(root, "first.bin"),
                new byte[17],
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(root, "second.bin"),
                new byte[29],
                TestContext.Current.CancellationToken);

            var result = DockerStorageMaintenanceService.MeasureManagedPathSize(
                root,
                fileLimit: 1,
                TestContext.Current.CancellationToken);

            Assert.Contains(result.Bytes, new long[] { 17, 29 });
            Assert.False(result.IsComplete);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task RunMaintenanceAsync_DryRunAlwaysPreventsMutation(int policyVersion)
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, "Reclaimable: 1GB\nTotal: 2GB\n", string.Empty));
        var service = Build(shell, new DockerStorageMaintenanceOptions
        {
            PolicyVersion = policyVersion,
            DryRun = true,
            PressureUsedPercent = 100,
            MinFreeSpaceGiB = 0,
            NuGetCacheRetentionDays = 0
        });

        await service.RunMaintenanceAsync("policy migration", TestContext.Current.CancellationToken);

        var pruneCalls = shell.ReceivedCalls()
            .Count(call => call.GetArguments().Length > 1
                && call.GetArguments()[1] is IReadOnlyList<string> args
                && args.Contains("prune"));
        Assert.Equal(0, pruneCalls);
    }

    [Theory]
    [InlineData("docker build -t app .")]
    [InlineData("docker buildx build --load .")]
    [InlineData("docker compose -f app.yml build api")]
    [InlineData("docker compose up -d --build")]
    [InlineData("COMPOSE=\"docker compose -f app.yml\"\n$COMPOSE up -d --build")]
    [InlineData("COMPOSE=\"docker compose -f app.yml\"\n${COMPOSE} build api")]
    [InlineData("sudo docker build -t app .")]
    [InlineData("sudo -n /usr/bin/docker buildx build --load .")]
    [InlineData("env X=1 docker build -t app .")]
    [InlineData("/usr/bin/env -i X=1 /usr/bin/docker compose up -d --build")]
    [InlineData("sudo env X=1 docker compose build api")]
    [InlineData("command docker build -t app .")]
    [InlineData("bash -c 'docker build -t app .'")]
    [InlineData("sudo sh -lc \"env X=1 docker compose up -d --build\"")]
    [InlineData("env X=1 bash -c 'sudo docker buildx build --load .'")]
    [InlineData("bash -c 'sh -c \"docker build -t app .\"'")]
    public void IsBuildCommand_DetectsDockerBuildMutations(string command)
    {
        var service = Build(Substitute.For<IShellRunner>());

        Assert.True(service.IsBuildCommand(command));
        Assert.False(service.IsBuildCommand("docker load -i app.tar && docker compose up -d --no-build"));
    }

    [Theory]
    [InlineData("sh -c 'sh -c \"sh -c \\\"docker build -t app .\\\"\"'")]
    [InlineData("sh -c 'sh -c \"sh -c \\\"sh -c \\\\\\\"docker build -t app .\\\\\\\"\\\"\"'")]
    public void IsBuildCommand_DeepShellWrappers_CannotBypassDeploymentGuard(string command)
    {
        var service = Build(Substitute.For<IShellRunner>(), new DockerStorageMaintenanceOptions
        {
            DeploymentOnly = true,
            AllowBuildsOnDeploymentTarget = false
        });

        Assert.True(service.IsBuildCommand(command));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public void IsBuildCommand_AtAndBeyondWrapperCeiling_FailsClosed(int wrapperCount)
    {
        var service = Build(Substitute.For<IShellRunner>(), new DockerStorageMaintenanceOptions
        {
            DeploymentOnly = true,
            AllowBuildsOnDeploymentTarget = false
        });
        var command = "docker build -t app .";
        for (var depth = 0; depth < wrapperCount; depth++)
            command = $"sh -c \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

        Assert.True(service.IsBuildCommand(command));
    }

    [Theory]
    [InlineData("echo docker build")]
    [InlineData("printf 'docker build'")]
    [InlineData("sudo docker load -i app.tar")]
    [InlineData("env X=1 docker compose up -d --no-build")]
    public void IsBuildCommand_DoesNotTreatNonBuildCommandsAsBuilds(string command)
    {
        var service = Build(Substitute.For<IShellRunner>());

        Assert.False(service.IsBuildCommand(command));
    }

    [Fact]
    public void IsBuildCommand_LargeMultilinePipelineScript_DetectsLateDockerBuild()
    {
        var service = Build(Substitute.For<IShellRunner>());
        var preamble = string.Join('\n', Enumerable.Repeat(
            "echo \"$WORKSPACE/$AGENT_WORK_DIRECTORY/$COMPOSE_PROJECT_NAME\" >/dev/null", 250));
        var command = $"{preamble}\ndocker build -f deploy/docker/Dockerfile.e2e -t aetheus-e2e .";

        Assert.True(service.IsBuildCommand(command));
    }

    [Fact]
    public void IsBuildCommand_RegexTimeout_FailsClosedAsBuild()
    {
        var service = Build(Substitute.For<IShellRunner>());
        var command = string.Concat(Enumerable.Repeat("env X=1 ", 50_000)) + "not-a-docker-command";

        Assert.True(service.IsBuildCommand(command));
    }

    [Theory]
    [InlineData(" docker build -t app .")]
    [InlineData("DOCKER_BUILDKIT=1 docker build -t app .")]
    [InlineData("docker --context production build -t app .")]
    [InlineData("time docker buildx build --load .")]
    [InlineData("if docker build -t app .; then echo done; fi")]
    [InlineData("docker.exe compose up -d --build")]
    [InlineData("bash -c $'docker build -t app .'")]
    public void IsBuildCommand_DeploymentOnlyConservativelyDetectsCommonWrapperBypasses(string command)
    {
        var service = Build(Substitute.For<IShellRunner>(), new DockerStorageMaintenanceOptions
        {
            DeploymentOnly = true,
            AllowBuildsOnDeploymentTarget = false
        });

        Assert.True(service.IsBuildCommand(command));
        Assert.False(service.IsBuildCommand("docker load -i app.tar && docker compose up -d --no-build"));
    }

    [Fact]
    public async Task PrepareBuildAsync_DeploymentOnlyAgent_FailsBeforeExecutingDocker()
    {
        var shell = Substitute.For<IShellRunner>();
        var service = Build(shell, new DockerStorageMaintenanceOptions
        {
            DeploymentOnly = true,
            AllowBuildsOnDeploymentTarget = false
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareBuildAsync(new Dictionary<string, string>(), TestContext.Current.CancellationToken));

        Assert.Contains("deployment-only", error.Message, StringComparison.Ordinal);
        await shell.DidNotReceiveWithAnyArgs().RunExecAsync(default!, default!, TestContext.Current.CancellationToken, default);

        shell.RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(127, string.Empty, "docker unavailable"));
        var diagnostics = await service.CollectDiagnosticsAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(diagnostics.LastBuildAttemptAtUtc);
    }

    [Fact]
    public async Task RunMaintenanceAsync_TargetsOnlyDedicatedBuilderWithBoundedPrune()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, "Reclaimable: 12.5GB\nTotal: 20GB\n", string.Empty));
        var service = Build(shell, new DockerStorageMaintenanceOptions
        {
            DryRun = false,
            BuilderName = "aetheus-build-01",
            MaxCacheAgeHours = 168,
            PressureUsedPercent = 100,
            ReservedSpaceGiB = 20,
            MaxCacheGiB = 80,
            MinFreeSpaceGiB = 20,
            NuGetCacheRetentionDays = 0
        });

        await service.RunMaintenanceAsync("test", TestContext.Current.CancellationToken);

        await shell.Received(1).RunExecAsync("docker",
            Arg.Is<IReadOnlyList<string>>(args =>
                args.SequenceEqual(new[]
                {
                    "buildx", "prune", "--builder", "aetheus-build-01", "--force", "--filter", "until=168h",
                    "--reserved-space", "20GB", "--max-used-space", "80GB", "--min-free-space", "20GB"
                })),
            Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
        await shell.DidNotReceive().RunExecAsync("docker",
            Arg.Is<IReadOnlyList<string>>(args => args.Contains("system") || args.Contains("volume") || args.Contains("image")),
            Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Theory]
    [InlineData("511.7MB", 511700000L)]
    [InlineData("1.5GiB", 1610612736L)]
    [InlineData("4.0G", 4000000000L)]
    [InlineData("211GB*", 211000000000L)]
    [InlineData("invalid", 0L)]
    public void ParseSizeBytes_ParsesDockerHumanSizes(string value, long expected) =>
        Assert.Equal(expected, DockerStorageMaintenanceService.ParseSizeBytes(value));

    [Fact]
    public void ResolveBuilderName_IsStableAndSanitized()
    {
        Assert.Equal("aetheus-build-agent-01", DockerStorageMaintenanceService.ResolveBuilderName(null, "Build Agent 01"));
        Assert.Equal("custom.builder", DockerStorageMaintenanceService.ResolveBuilderName("Custom.Builder", "ignored"));
    }

    [Theory]
    [InlineData(80, 30, true)]
    [InlineData(79, 19, true)]
    [InlineData(79, 21, false)]
    public void ShouldUseAggressivePolicy_AppliesPercentAndAbsoluteFloor(int used, int freeGiB, bool expected)
    {
        var options = new DockerStorageMaintenanceOptions { PressureUsedPercent = 80, MinFreeSpaceGiB = 20 };
        Assert.Equal(expected, DockerStorageMaintenanceService.ShouldUseAggressivePolicy(
            used, (long)freeGiB * 1024 * 1024 * 1024, options));
    }

    [Fact]
    public async Task RunMaintenanceAsync_NuGetRetention_RemovesOnlyExpiredNonLatestVersions()
    {
        var root = Directory.CreateTempSubdirectory("aetheus-nuget-retention-");
        try
        {
            var oldVersion = Directory.CreateDirectory(Path.Combine(root.FullName, "sample.package", "1.0.0"));
            var latestVersion = Directory.CreateDirectory(Path.Combine(root.FullName, "sample.package", "2.0.0"));
            await File.WriteAllTextAsync(Path.Combine(oldVersion.FullName, ".nupkg.metadata"), "{}", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(latestVersion.FullName, ".nupkg.metadata"), "{}", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(oldVersion.FullName, "old.dll"), "old", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(latestVersion.FullName, "new.dll"), "new", TestContext.Current.CancellationToken);
            Directory.SetLastWriteTimeUtc(oldVersion.FullName, DateTime.UtcNow.AddDays(-60));

            var shell = Substitute.For<IShellRunner>();
            shell.RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
                .Returns(new ShellExecResult(0, "Reclaimable: 0B\nTotal: 0B\n", string.Empty));
            var service = Build(shell, new DockerStorageMaintenanceOptions
            {
                DryRun = false,
                PressureUsedPercent = 100,
                NuGetPackagesPath = root.FullName,
                NuGetCacheRetentionDays = 30
            });

            await service.RunMaintenanceAsync("test", TestContext.Current.CancellationToken);

            Assert.False(Directory.Exists(oldVersion.FullName));
            Assert.True(Directory.Exists(latestVersion.FullName));
        }
        finally
        {
            if (root.Exists) root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunMaintenanceAsync_NuGetRetention_RefusesDirectoriesWithoutNuGetMetadata()
    {
        var root = Directory.CreateTempSubdirectory("aetheus-nuget-unsafe-");
        try
        {
            var oldVersion = Directory.CreateDirectory(Path.Combine(root.FullName, "not-a-package", "1.0.0"));
            var latestVersion = Directory.CreateDirectory(Path.Combine(root.FullName, "not-a-package", "2.0.0"));
            await File.WriteAllTextAsync(Path.Combine(oldVersion.FullName, "must-survive.txt"), "operator-data",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(latestVersion.FullName, "also-survives.txt"), "operator-data",
                TestContext.Current.CancellationToken);
            Directory.SetLastWriteTimeUtc(oldVersion.FullName, DateTime.UtcNow.AddDays(-60));

            var shell = Substitute.For<IShellRunner>();
            shell.RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
                .Returns(new ShellExecResult(0, "Reclaimable: 0B\nTotal: 0B\n", string.Empty));
            var service = Build(shell, new DockerStorageMaintenanceOptions
            {
                DryRun = false,
                PressureUsedPercent = 100,
                NuGetPackagesPath = root.FullName,
                NuGetCacheRetentionDays = 30
            });

            await service.RunMaintenanceAsync("test", TestContext.Current.CancellationToken);

            Assert.True(File.Exists(Path.Combine(oldVersion.FullName, "must-survive.txt")));
            Assert.True(File.Exists(Path.Combine(latestVersion.FullName, "also-survives.txt")));
        }
        finally
        {
            if (root.Exists) root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunMaintenanceAsync_WhileBuildOwnsLock_SkipsConcurrentCleanup()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, "Reclaimable: 1GB\nTotal: 2GB\n", string.Empty));
        var service = Build(shell, new DockerStorageMaintenanceOptions
        {
            PolicyVersion = 2,
            DryRun = true,
            PressureUsedPercent = 100,
            MinFreeSpaceGiB = 0,
            NuGetCacheRetentionDays = 0
        });

        var environment = new Dictionary<string, string>();
        await service.PrepareBuildAsync(environment, TestContext.Current.CancellationToken);
        Assert.Equal(service.BuilderName, environment["AETHEUS_BUILDX_BUILDER"]);
        Assert.Equal(service.BuilderName, environment["BUILDX_BUILDER"]);
        await service.RunMaintenanceAsync("concurrent", TestContext.Current.CancellationToken);

        await shell.DidNotReceive().RunExecAsync("docker",
            Arg.Is<IReadOnlyList<string>>(args => args.Take(2).SequenceEqual(new[] { "buildx", "du" })),
            Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());

        await service.CompleteBuildAsync(TestContext.Current.CancellationToken);

        await shell.Received(1).RunExecAsync("docker",
            Arg.Is<IReadOnlyList<string>>(args => args.Take(2).SequenceEqual(new[] { "buildx", "du" })),
            Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task CompleteBuildAsync_WhenCancelled_ReleasesLockForNextBuild()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(call =>
            {
                call.ArgAt<CancellationToken>(2).ThrowIfCancellationRequested();
                return new ShellExecResult(0, "Reclaimable: 0B\nTotal: 0B\n", string.Empty);
            });
        var service = Build(shell, new DockerStorageMaintenanceOptions
        {
            PolicyVersion = 2,
            DryRun = true,
            PressureUsedPercent = 100,
            MinFreeSpaceGiB = 0,
            NuGetCacheRetentionDays = 0
        });
        await service.PrepareBuildAsync(new Dictionary<string, string>(), TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CompleteBuildAsync(cancelled.Token));
        await service.PrepareBuildAsync(new Dictionary<string, string>(), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await service.CompleteBuildAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReadPendingCompletionMarkerAsync_WhenRemovedConcurrently_ReturnsMissing()
    {
        var root = Directory.CreateTempSubdirectory("aetheus-maintenance-marker-");
        try
        {
            var marker = Path.Combine(root.FullName, ".docker-maintenance-pending");

            var result = await DockerStorageMaintenanceService.ReadPendingCompletionMarkerAsync(
                marker,
                TestContext.Current.CancellationToken);

            Assert.Null(result);
        }
        finally
        {
            if (root.Exists) root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunMaintenanceAsync_WhenDockerUnavailable_FailsObservablyWithoutGlobalMutation()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(127, string.Empty, "docker: command not found"));
        var service = Build(shell, new DockerStorageMaintenanceOptions
        {
            DryRun = false,
            PressureUsedPercent = 100,
            MinFreeSpaceGiB = 0,
            NuGetCacheRetentionDays = 0
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunMaintenanceAsync("docker unavailable", TestContext.Current.CancellationToken));

        Assert.Contains("Could not inventory dedicated Buildx builder", error.Message, StringComparison.Ordinal);
        await shell.DidNotReceive().RunExecAsync("docker",
            Arg.Is<IReadOnlyList<string>>(args => args.Contains("system") || args.Contains("image") || args.Contains("volume")),
            Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task CollectDiagnosticsAsync_ReportsDockerAvailabilityWithoutInventingMeasurements(
        int exitCode, bool expectedAvailable)
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(exitCode,
                exitCode == 0 ? "Images\t2GB\t1GB\nLocal Volumes\t3GB\t0B\nReclaimable: 1GB\nTotal: 4GB\n" : string.Empty,
                exitCode == 0 ? string.Empty : "permission denied"));
        var service = Build(shell);

        var diagnostics = await service.CollectDiagnosticsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedAvailable, diagnostics.BuildCacheAvailable);
        Assert.Equal(expectedAvailable, diagnostics.DockerInventoryAvailable);
        Assert.Equal(expectedAvailable ? 4_000_000_000L : 0L, diagnostics.BuildCacheBytes);
        Assert.Equal(expectedAvailable ? 2_000_000_000L : 0L, diagnostics.DockerImagesBytes);
    }

    [Fact]
    public async Task PrepareBuildAsync_WhenDiskRemainsBelowSafetyFloor_RefusesBuildAfterAggressivePrune()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, "Reclaimable: 1GB\nTotal: 2GB\n", string.Empty));
        var service = Build(shell, new DockerStorageMaintenanceOptions
        {
            DryRun = false,
            MinFreeSpaceGiB = 100_000,
            PressureCacheAgeHours = 24,
            NuGetCacheRetentionDays = 0
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareBuildAsync(new Dictionary<string, string>(), TestContext.Current.CancellationToken));

        Assert.Contains("refused after bounded cache maintenance", error.Message, StringComparison.Ordinal);
        await shell.Received(1).RunExecAsync("docker",
            Arg.Is<IReadOnlyList<string>>(args => args.Contains("until=24h")),
            Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Theory]
    [InlineData(0, "123456789\t/var/lib/aetheus/work\n", 123_456_789L, true)]
    [InlineData(0, "  42 /srv", 42L, true)]
    [InlineData(1, "123 /srv", 0L, false)]
    [InlineData(0, "", 0L, false)]
    [InlineData(0, "-5 /srv", 0L, false)]
    [InlineData(0, "du: cannot read directory", 0L, false)]
    public async Task MeasureWithDuAsync_ReadsTheByteCountOfASuccessfulDu(
        int exitCode, string stdout, long expectedBytes, bool expectedComplete)
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("du", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(exitCode, stdout, string.Empty));

        var measurement = await Build(shell).MeasureWithDuAsync("/srv/data", TestContext.Current.CancellationToken);

        Assert.Equal(expectedBytes, measurement.Bytes);
        Assert.Equal(expectedComplete, measurement.IsComplete);
        await shell.Received(1).RunExecAsync(
            "du",
            Arg.Is<IReadOnlyList<string>>(args => args.SequenceEqual(new[] { "-sb", "--", "/srv/data" })),
            Arg.Any<CancellationToken>(),
            TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task MeasureWithDuAsync_ShellDoubleReturningNull_IsAnUnknownSize()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("du", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(Task.FromResult<ShellExecResult>(null!));

        var measurement = await Build(shell).MeasureWithDuAsync("/srv/data", TestContext.Current.CancellationToken);

        Assert.Equal(0, measurement.Bytes);
        Assert.False(measurement.IsComplete);
    }

    private static DockerStorageMaintenanceService Build(
        IShellRunner shell, DockerStorageMaintenanceOptions? storage = null) => new(
        shell,
        Options.Create(new AetheusAgentOptions
        {
            Name = "agent-01",
            DockerStorageMaintenance = storage ?? new DockerStorageMaintenanceOptions()
        }),
        TimeProvider.System,
        NullLogger<DockerStorageMaintenanceService>.Instance);
}
