// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

internal static class BackgroundServiceTestKit
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>A scope factory that hands out the given instances as scoped services.</summary>
    public static IServiceScopeFactory ScopeFactory(params (Type Type, object Instance)[] services)
    {
        var collection = new ServiceCollection();
        foreach (var (type, instance) in services) collection.AddScoped(type, _ => instance);
        return collection.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>A scope factory whose service is created by <paramref name="create"/> at each resolution.</summary>
    public static IServiceScopeFactory ScopeFactoryOf(Type type, Func<object> create)
    {
        var collection = new ServiceCollection();
        collection.AddScoped(type, _ => create());
        return collection.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>A lease that never becomes leader: it completes <c>Invoked</c> with the lease name and runs no work.</summary>
    public static (IPostgresLeaderLease Lease, Task<string> Invoked) NonLeaderLease()
    {
        var invoked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = Substitute.For<IPostgresLeaderLease>();
        lease.RunAsLeaderAsync(Arg.Any<string>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                invoked.TrySetResult(call.Arg<string>());
                return Task.CompletedTask;
            });
        return (lease, invoked.Task);
    }

    /// <summary>
    /// A lease that is leader at once. <c>Started</c> is released once the leader work has run up to its first
    /// wait, so a test that advances a <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/> after it
    /// is sure the loop's timer exists.
    /// </summary>
    public static (IPostgresLeaderLease Lease, SemaphoreSlim Started) LeaderLease()
    {
        var started = new SemaphoreSlim(0);
        var lease = Substitute.For<IPostgresLeaderLease>();
        lease.RunAsLeaderAsync(Arg.Any<string>(), Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>());
                started.Release();
                return work;
            });
        return (lease, started);
    }

    /// <summary>Waits for the lease to be requested and returns its name.</summary>
    public static async Task<string> NameAsync(this Task<string> invoked, CancellationToken ct) =>
        await invoked.WaitAsync(Bound, ct);
    /// <summary>Waits for one signal, failing the test if it never comes (a bound, not a sleep).</summary>
    public static async Task AwaitAsync(this SemaphoreSlim signal, CancellationToken ct) =>
        Assert.True(await signal.WaitAsync(Bound, ct), "the background service did not reach the awaited point");
}
