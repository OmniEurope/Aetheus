// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.SystemLogs;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OmniEurope.Diagnostics.Logs;

namespace Aetheus.Back.Tests;

/// <summary>Verifies that audit and system log pages receive data changes through the admin
/// hub.</summary>
public sealed class LiveLogPushTests
{
    [Fact]
    public async Task AuditEntry_IsPushedOnceWritten()
    {
        var notifier = Substitute.For<IAdminChangeNotifier>();
        var http = Substitute.For<IHttpContextAccessor>();
        http.HttpContext.Returns(new DefaultHttpContext());
        var sut = new AuditService(Substitute.For<IAuditRepository>(), http, Substitute.For<IAuditChainService>(),
            Substitute.For<IMemoryCache>(), TimeProvider.System, notifier);

        await sut.LogAsync("Created", "Server", 7, ct: TestContext.Current.CancellationToken);

        await notifier.Received(1).BroadcastAsync(AdminEntities.AuditLog, Arg.Any<int>(), EntityChangeOps.Created, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuditEntry_IsKept_WhenThePushFails()
    {
        var notifier = Substitute.For<IAdminChangeNotifier>();
        notifier.BroadcastAsync(default!, default, default!, TestContext.Current.CancellationToken).ReturnsForAnyArgs(Task.FromException(new InvalidOperationException("hub down")));
        var repo = Substitute.For<IAuditRepository>();
        var http = Substitute.For<IHttpContextAccessor>();
        http.HttpContext.Returns(new DefaultHttpContext());
        var sut = new AuditService(repo, http, Substitute.For<IAuditChainService>(), Substitute.For<IMemoryCache>(), TimeProvider.System, notifier);

        await sut.LogAsync("Created", "Server", 7, ct: TestContext.Current.CancellationToken);

        await repo.Received(1).AddAsync(Arg.Any<AuditLog>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SystemLogChanges_AreFoldedIntoOnePush_AndNothingIsPushedWhenNothingChanged()
    {
        var notifier = Substitute.For<IAdminChangeNotifier>();
        var sut = new SystemLogChangeBroadcaster(Substitute.For<ILogChanges>(), notifier,
            NullLogger<SystemLogChangeBroadcaster>.Instance);
        using var nothing = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        nothing.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.PushNextChangeAsync(nothing.Token));
        sut.MarkChanged();
        sut.MarkChanged();
        sut.MarkChanged();
        await sut.PushNextChangeAsync(TestContext.Current.CancellationToken);
        using var afterwards = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        afterwards.CancelAfter(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.PushNextChangeAsync(afterwards.Token));

        await notifier.Received(1).BroadcastAsync(AdminEntities.SystemLog, 0, EntityChangeOps.Updated, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SystemLogChanges_OfThePackageSignal_ArePushedToTheAdminHub_UntilTheServiceStops()
    {
        var changes = Substitute.For<ILogChanges>();
        var notifier = Substitute.For<IAdminChangeNotifier>();
        var pushed = new SemaphoreSlim(0);
        notifier.BroadcastAsync(AdminEntities.SystemLog, 0, EntityChangeOps.Updated, Arg.Any<CancellationToken>())
            .Returns(_ => { pushed.Release(); return Task.CompletedTask; });
        using var sut = new SystemLogChangeBroadcaster(changes, notifier, NullLogger<SystemLogChangeBroadcaster>.Instance);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        // Since .NET 10 a BackgroundService runs ExecuteAsync on a background thread, so StartAsync can return before the
        // service subscribes to the signal: raise it until a push comes, within the same 10 s bound.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var delivered = false;
        while (!delivered && DateTime.UtcNow < deadline)
        {
            changes.Changed += Raise.Event();
            delivered = await pushed.WaitAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }
        Assert.True(delivered);
        await sut.StopAsync(TestContext.Current.CancellationToken);
        var pushesWhileRunning = notifier.ReceivedCalls().Count();
        while (pushed.CurrentCount > 0) await pushed.WaitAsync(TestContext.Current.CancellationToken);

        // Stopped: the service no longer listens to the signal, so the package stops watching the folder.
        changes.Changed += Raise.Event();
        Assert.False(await pushed.WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
        Assert.Equal(pushesWhileRunning, notifier.ReceivedCalls().Count());
    }
}
