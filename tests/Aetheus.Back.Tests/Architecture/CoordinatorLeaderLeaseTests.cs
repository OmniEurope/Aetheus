// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Architecture;

public sealed class CoordinatorLeaderLeaseTests
{
    [Fact]
    public async Task AiScheduler_UsesDedicatedLeaderLease()
    {
        var (lease, invoked) = SignalingCompletedLease();
        var service = new AiTaskSchedulerService(
            Substitute.For<IServiceScopeFactory>(),
            TimeProvider.System,
            Substitute.For<ILogger<AiTaskSchedulerService>>(),
            lease);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await invoked.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await lease.Received(1).RunAsLeaderAsync(
            "aetheus:ai-task-scheduler",
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentUpdateCoordinator_UsesDedicatedLeaderLease()
    {
        var (lease, invoked) = SignalingCompletedLease();
        var service = new AgentUpdateCoordinatorService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<IHubContext<ServerHub>>(),
            TimeProvider.System,
            NullLogger<AgentUpdateCoordinatorService>.Instance,
            lease);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await invoked.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await lease.Received(1).RunAsLeaderAsync(
            "aetheus:agent-update-coordinator",
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R462_AppTelemetryRetention_UsesDedicatedLeaderLease()
    {
        var (lease, invoked) = SignalingCompletedLease();
        var service = new AppTelemetryRetentionService(
            Substitute.For<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<AppTelemetryRetentionService>.Instance,
            TimeProvider.System,
            lease);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await invoked.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await lease.Received(1).RunAsLeaderAsync(
            "aetheus:app-telemetry-retention",
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R2016_ArtifactStorageMonitor_UsesDedicatedLeaderLease()
    {
        var (lease, invoked) = SignalingCompletedLease();
        var service = new ArtifactStorageMonitorService(
            new ConfigurationBuilder().Build(),
            Substitute.For<IHubContext<AlertHub>>(),
            Substitute.For<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<ArtifactStorageMonitorService>.Instance,
            lease);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await invoked.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await lease.Received(1).RunAsLeaderAsync(
            "aetheus:artifact-storage-monitor",
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R463_ArtifactCleanup_UsesDedicatedLeaderLease()
    {
        var (lease, invoked) = SignalingCompletedLease();
        var service = new ArtifactCleanupService(
            Substitute.For<IServiceScopeFactory>(),
            NullLogger<ArtifactCleanupService>.Instance,
            TimeProvider.System,
            lease,
            Substitute.For<IChunkedArtifactUploadService>());

        await service.StartAsync(TestContext.Current.CancellationToken);
        await invoked.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await lease.Received(1).RunAsLeaderAsync(
            "aetheus:artifact-cleanup",
            Arg.Any<Func<CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    private static (IPostgresLeaderLease Lease, Task Invoked) SignalingCompletedLease()
    {
        var lease = Substitute.For<IPostgresLeaderLease>();
        var invoked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lease.RunAsLeaderAsync(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                invoked.TrySetResult();
                return Task.CompletedTask;
            });
        return (lease, invoked.Task);
    }
}
