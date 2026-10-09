// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.SystemLogs;
using Aetheus.Back.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.SystemLogs;

/// <summary>R-181: the push that replaces the Refresh button of the administration Performance page.</summary>
public sealed class ApiPerformanceChangeBroadcasterTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
    private readonly IAdminChangeNotifier _notifier = Substitute.For<IAdminChangeNotifier>();
    private readonly RequestDurationFeed _feed;
    private readonly ApiPerformanceChangeBroadcaster _sut;

    public ApiPerformanceChangeBroadcasterTests()
    {
        // The collector as Aetheus configures it: 24 hours, API routes only.
        _feed = new RequestDurationFeed(ApiPerformanceReport.Configure, _time);
        _sut = new ApiPerformanceChangeBroadcaster(
            _feed.Recorder, _notifier, _time, NullLogger<ApiPerformanceChangeBroadcaster>.Instance);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _feed.Dispose();
    }

    [Fact]
    public async Task ARecordedSample_IsAnnouncedOnce_ToTheAdminHub()
    {
        _feed.Request("api/projects", "GET", 200, 12);
        _feed.Request("api/pipelines/{id}", "GET", 200, 30);

        await _sut.AnnounceNextChangeAsync(TestContext.Current.CancellationToken);

        await _notifier.Received(1).BroadcastAsync(
            AdminEntities.ApiPerformance, 0, EntityChangeOps.Updated, Arg.Any<CancellationToken>());
        // Both samples collapsed into that single announcement: nothing is pending any more.
        Assert.False(_feed.Recorder.WaitForChangeAsync(TestContext.Current.CancellationToken).IsCompleted);
    }

    [Fact]
    public void NoTraffic_AnnouncesNothing()
    {
        var announce = _sut.AnnounceNextChangeAsync(TestContext.Current.CancellationToken);

        Assert.False(announce.IsCompleted);
        _notifier.DidNotReceive().BroadcastAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void StaticFilesProbesAndTheSpaFallback_AreNeitherMeasuredNorAnnounced()
    {
        _feed.Request("health", "GET", 200, 1);
        _feed.Request("{*path:nonfile}", "GET", 200, 3);

        Assert.False(_feed.Recorder.WaitForChangeAsync(TestContext.Current.CancellationToken).IsCompleted);
        Assert.Equal(0, _feed.Recorder.Summarize().SampleCount);
    }

    [Fact]
    public async Task AFailedPush_DoesNotStopTheAnnouncements()
    {
        _notifier.BroadcastAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("hub down")), Task.CompletedTask);

        _feed.Request("api/projects", "GET", 200, 12);
        await _sut.AnnounceNextChangeAsync(TestContext.Current.CancellationToken);
        _feed.Request("api/projects", "GET", 200, 14);
        await _sut.AnnounceNextChangeAsync(TestContext.Current.CancellationToken);

        await _notifier.Received(2).BroadcastAsync(
            AdminEntities.ApiPerformance, 0, EntityChangeOps.Updated, Arg.Any<CancellationToken>());
    }
}
