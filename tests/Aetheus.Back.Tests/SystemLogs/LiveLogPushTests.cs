// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.SystemLogs;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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
    public async Task SystemLogChanges_ArePushedOncePerTick_AndNotWhenNothingChanged()
    {
        var notifier = Substitute.For<IAdminChangeNotifier>();
        var sut = new SystemLogChangeBroadcaster(new ConfigurationBuilder().Build(), notifier, TimeProvider.System,
            NullLogger<SystemLogChangeBroadcaster>.Instance);

        Assert.False(await sut.FlushAsync(TestContext.Current.CancellationToken));
        sut.MarkChanged();
        sut.MarkChanged();
        sut.MarkChanged();
        Assert.True(await sut.FlushAsync(TestContext.Current.CancellationToken));
        Assert.False(await sut.FlushAsync(TestContext.Current.CancellationToken));

        await notifier.Received(1).BroadcastAsync(AdminEntities.SystemLog, 0, EntityChangeOps.Updated, Arg.Any<CancellationToken>());
    }
}
