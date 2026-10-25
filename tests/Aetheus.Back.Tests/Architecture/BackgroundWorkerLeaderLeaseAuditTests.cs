// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.SystemLogs;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>Hosted workers require a PostgreSQL leader lease so only the live colour performs
/// shared work. Explicitly listed process-local workers retain their documented
/// exceptions.</summary>
public sealed class BackgroundWorkerLeaderLeaseAuditTests
{
    private static readonly Dictionary<Type, string> PerInstanceWorkers = new()
    {
        [typeof(LiveInstanceProbe)] = "measures this process's own serving state",
        [typeof(BackgroundTaskQueueHostedService)] = "drains this process's in-memory queue",
        [typeof(SystemLogChangeBroadcaster)] = "pushes this process's own log file to its SignalR clients",
        [typeof(ApiPerformanceChangeBroadcaster)] = "pushes this process's own request timings to its SignalR clients",
    };

    [Fact]
    public void EveryHostedWorker_TakesALeaderLease_OrIsPerInstanceForAStatedReason()
    {
        var workers = typeof(PostgresLeaderLease).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(BackgroundService).IsAssignableFrom(type))
            .ToList();
        Assert.NotEmpty(workers);

        var unleased = workers
            .Where(type => !PerInstanceWorkers.ContainsKey(type))
            .Where(type => !type.GetConstructors().Any(ctor =>
                ctor.GetParameters().Any(parameter => parameter.ParameterType == typeof(IPostgresLeaderLease))))
            .Select(type => type.FullName)
            .ToList();

        Assert.True(unleased.Count == 0,
            "These background workers run in both blue-green colours; give them an IPostgresLeaderLease "
            + "or list them as per-instance with a reason: " + string.Join(", ", unleased));
    }

    [Fact]
    public async Task APeriodicWorker_RunsUnderItsOwnLeaseName()
    {
        var lease = Substitute.For<IPostgresLeaderLease>();
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.RunAsLeaderAsync(Arg.Any<string>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                invoked.TrySetResult();
                return Task.CompletedTask;
            });
        var worker = new MetricsCleanupService(
            Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<MetricsCleanupService>.Instance,
            TimeProvider.System,
            lease);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await lease.Received(1).RunAsLeaderAsync(
            "aetheus:metrics-cleanup", Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
    }
}
