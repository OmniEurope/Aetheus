// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The post-build finalization of the Docker storage maintenance: a finished build queues its
/// completion (and writes a marker so a restart can resume it), the background loop consumes the queue
/// and releases the build gate, a marker left by a previous run is resumed at start, and the periodic
/// pass runs when nothing else is queued.</summary>
public sealed class DockerStorageMaintenanceBuildCompletionTests : IDisposable
{
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("aetheus-maintenance-completion-");
    private readonly IShellRunner _shell = Substitute.For<IShellRunner>();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public DockerStorageMaintenanceBuildCompletionTests() =>
        _shell.RunExecAsync("docker", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, "Reclaimable: 1GB\nTotal: 2GB\n", string.Empty));

    public void Dispose() => _work.Delete(recursive: true);

    private string Marker => Path.Combine(_work.FullName, ".docker-maintenance-pending");

    private DockerStorageMaintenanceService Build(string? workDirectory = null, TimeProvider? time = null) => new(
        _shell,
        Options.Create(new AetheusAgentOptions
        {
            Name = "agent-01",
            WorkDirectory = workDirectory ?? _work.FullName,
            DockerStorageMaintenance = new DockerStorageMaintenanceOptions
            {
                PolicyVersion = 2,
                DryRun = true,
                PressureUsedPercent = 100,
                MinFreeSpaceGiB = 0,
                NuGetCacheRetentionDays = 0
            }
        }),
        time ?? TimeProvider.System,
        NullLogger<DockerStorageMaintenanceService>.Instance);

    private int BuilderInventories() => _shell.ReceivedCalls().Count(call =>
        call.GetMethodInfo().Name == nameof(IShellRunner.RunExecAsync)
        && call.GetArguments()[1] is IReadOnlyList<string> args && args.Take(2).SequenceEqual(new[] { "buildx", "du" }));

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20, ct);
        }
        return condition();
    }

    [Fact]
    public async Task ScheduleBuildCompletion_WithNoBuildInProgress_WritesNothing()
    {
        var service = Build();

        await service.ScheduleBuildCompletionAsync(runDockerMaintenance: true, _ct);

        Assert.False(File.Exists(Marker));
        Assert.False(service.BuildActive);
    }

    [Theory]
    [InlineData(true, "docker")]
    [InlineData(false, "lease")]
    public async Task ScheduleBuildCompletion_PersistsWhatToResumeAfterARestart(bool runDockerMaintenance, string expectedMarker)
    {
        var service = Build();
        await service.PrepareBuildAsync(new Dictionary<string, string>(), _ct);

        await service.ScheduleBuildCompletionAsync(runDockerMaintenance, _ct);

        Assert.Equal(expectedMarker, await File.ReadAllTextAsync(Marker, _ct));
        Assert.True(service.BuildActive);
    }

    [Fact]
    public async Task ScheduleBuildCompletion_WhenTheMarkerCannotBeWritten_StillQueuesTheCompletion()
    {
        var blockingFile = Path.Combine(_work.FullName, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "x", _ct);
        var service = Build(workDirectory: Path.Combine(blockingFile, "nested"));
        await service.PrepareBuildAsync(new Dictionary<string, string>(), _ct);

        await service.ScheduleBuildCompletionAsync(runDockerMaintenance: false, _ct);
        await service.StartAsync(_ct);
        try
        {
            Assert.True(await WaitUntilAsync(() => !service.BuildActive, _ct), "the queued completion released the build gate");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheBackgroundLoop_CompletesAQueuedBuild_RunsTheMaintenance_AndClearsTheMarker()
    {
        var service = Build();
        await service.StartAsync(_ct);
        try
        {
            await service.PrepareBuildAsync(new Dictionary<string, string>(), _ct);
            var inventoriesBefore = BuilderInventories();
            await service.ScheduleBuildCompletionAsync(runDockerMaintenance: true, _ct);

            Assert.True(await WaitUntilAsync(() => !service.BuildActive, _ct), "the build gate was released");
            Assert.False(File.Exists(Marker));
            Assert.True(BuilderInventories() > inventoriesBefore, "the post-build maintenance inventoried the builder");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheBackgroundLoop_CompletesALeaseOnlyBuild_WithoutRunningTheMaintenance()
    {
        var service = Build();
        await service.StartAsync(_ct);
        try
        {
            await service.PrepareBuildAsync(new Dictionary<string, string>(), _ct);
            var inventoriesBefore = BuilderInventories();
            await service.ScheduleBuildCompletionAsync(runDockerMaintenance: false, _ct);

            Assert.True(await WaitUntilAsync(() => !service.BuildActive && !File.Exists(Marker), _ct));
            Assert.Equal(inventoriesBefore, BuilderInventories());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ABuildCompletedBeforeTheLoopStarts_IsStillCompleted()
    {
        // The background loop starts asynchronously: a build can finish and queue its completion
        // (marker on disk, lock still held) before the loop's start-up resume reads that marker.
        var service = Build();
        await service.PrepareBuildAsync(new Dictionary<string, string>(), _ct);
        await service.ScheduleBuildCompletionAsync(runDockerMaintenance: false, _ct);

        await service.StartAsync(_ct);
        try
        {
            Assert.True(
                await WaitUntilAsync(() => !service.BuildActive && !File.Exists(Marker), _ct),
                "the queued completion released the build lock and removed its marker");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AtStart_AMarkerLeftByAPreviousRun_IsResumedAndRemoved()
    {
        await File.WriteAllTextAsync(Marker, "docker", _ct);
        var service = Build();

        await service.StartAsync(_ct);
        try
        {
            Assert.True(await WaitUntilAsync(() => !File.Exists(Marker), _ct), "the leftover marker was consumed");
            Assert.True(await WaitUntilAsync(() => BuilderInventories() > 0, _ct), "its maintenance ran");
            Assert.False(service.BuildActive);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheBackgroundLoop_RunsThePeriodicMaintenance_WhenNothingIsQueued()
    {
        var time = new FakeTimeProvider();
        var service = Build(time: time);

        await service.StartAsync(_ct);
        try
        {
            // Let the loop reach its first wait, then move the clock past the default 60-minute interval.
            await Task.Delay(100, _ct);
            Assert.Equal(0, BuilderInventories());
            time.Advance(TimeSpan.FromMinutes(61));

            Assert.True(await WaitUntilAsync(() => BuilderInventories() > 0, _ct), "the periodic pass inventoried the builder");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
