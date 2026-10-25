// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>The push that replaces the Refresh button of a monitored app's Logs and Errors
/// tabs.</summary>
public sealed class AppTelemetryChangePublisherTests : IDisposable
{
    private readonly IAppMonitoringRepository _apps = Substitute.For<IAppMonitoringRepository>();
    private readonly IEntityChangeNotifier _notifier = Substitute.For<IEntityChangeNotifier>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly AppTelemetryChangePublisher _sut;

    public AppTelemetryChangePublisherTests()
    {
        _apps.GetAppProjectIdAsync(7, Arg.Any<CancellationToken>()).Returns(42);
        _apps.GetProjectOrgIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [42] = 3 });
        _sut = new AppTelemetryChangePublisher(
            _apps, _notifier, _cache, NullLogger<AppTelemetryChangePublisher>.Instance);
    }

    public void Dispose() => _cache.Dispose();

    [Fact]
    public async Task Publish_TargetsTheOwningProject_WithItsOrganization()
    {
        await _sut.PublishAsync(7, TestContext.Current.CancellationToken);

        // Scoped exactly like the telemetry endpoints: Project read, including the org aggregate group.
        await _notifier.Received(1).BroadcastOperationalAsync(
            ResourceType.Project, 42, OperationalRealtimeEvents.AppTelemetryChanged,
            Arg.Any<CancellationToken>(), 3);
    }

    [Fact]
    public async Task Publish_CachesTheOwner_SoABatchStormDoesNotHitTheDatabase()
    {
        await _sut.PublishAsync(7, TestContext.Current.CancellationToken);
        await _sut.PublishAsync(7, TestContext.Current.CancellationToken);

        await _apps.Received(1).GetAppProjectIdAsync(7, Arg.Any<CancellationToken>());
        await _notifier.Received(2).BroadcastOperationalAsync(
            ResourceType.Project, 42, OperationalRealtimeEvents.AppTelemetryChanged,
            Arg.Any<CancellationToken>(), 3);
    }

    [Fact]
    public async Task Publish_UnknownApp_BroadcastsNothing()
    {
        _apps.GetAppProjectIdAsync(9, Arg.Any<CancellationToken>()).Returns((int?)null);

        await _sut.PublishAsync(9, TestContext.Current.CancellationToken);

        await _notifier.DidNotReceive().BroadcastOperationalAsync(
            Arg.Any<ResourceType>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task Publish_AFailedPush_DoesNotFailTheStoredIngestion()
    {
        _notifier.BroadcastOperationalAsync(
                Arg.Any<ResourceType>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .ThrowsAsync(new InvalidOperationException("hub down"));

        var exception = await Record.ExceptionAsync(() => _sut.PublishAsync(7, TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }
}
