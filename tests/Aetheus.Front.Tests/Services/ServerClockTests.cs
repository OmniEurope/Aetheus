// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Services;

/// <summary>
/// A running build showed "0s" for as long as the browser's clock lagged the server's, because the
/// duration subtracted a server timestamp from a browser "now" and the negative result was clamped to
/// zero. Measured against production on 2026-09-06: the browser was 19.9 s behind.
/// </summary>
[Collection("ServerClock")]
public sealed class ServerClockTests : IDisposable
{
    public void Dispose() => ServerClock.Reset();

    [Fact]
    public void UtcNow_BeforeAnyResponse_IsTheBrowserClock()
    {
        ServerClock.Reset();

        Assert.Equal(TimeSpan.Zero, ServerClock.Offset);
        Assert.True((ServerClock.UtcNow - DateTime.UtcNow).Duration() < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Observe_AServerAheadOfTheBrowser_MovesNowForward()
    {
        ServerClock.Observe(DateTimeOffset.UtcNow.AddSeconds(20), TimeSpan.Zero);

        Assert.InRange(ServerClock.Offset, TimeSpan.FromSeconds(19), TimeSpan.FromSeconds(21));
        Assert.InRange(ServerClock.UtcNow - DateTime.UtcNow, TimeSpan.FromSeconds(19), TimeSpan.FromSeconds(21));
    }

    [Fact]
    public void Observe_ChargesHalfTheRoundTripToTheResponseLeg()
    {
        ServerClock.Observe(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(4));

        Assert.InRange(ServerClock.Offset, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public void FormatDuration_WithAServerStartInTheBrowsersFuture_StillCounts()
    {
        // The exact reported symptom: the server started the run 15 s ago on its own clock, which is
        // 20 s ahead of this browser. Without the correction the difference is negative and clamps to 0s.
        ServerClock.Observe(DateTimeOffset.UtcNow.AddSeconds(20), TimeSpan.Zero);
        var startedOnTheServer = DateTime.UtcNow.AddSeconds(20).AddSeconds(-15);

        var formatted = PipelineRunFormatting.FormatDuration(startedOnTheServer, null);

        Assert.NotEqual("0s", formatted);
        Assert.Matches(@"^1[3-7]s$", formatted);
    }

    [Fact]
    public void FormatDuration_WithoutTheCorrection_IsTheBugThisFixes()
    {
        // Pins the regression: with no observed offset the same timestamps produce the clamped "0s".
        ServerClock.Reset();
        var startedOnTheServer = DateTime.UtcNow.AddSeconds(20).AddSeconds(-15);

        Assert.Equal("0s", PipelineRunFormatting.FormatDuration(startedOnTheServer, null));
    }

    [Fact]
    public void FormatDuration_ACompletedRun_IgnoresTheClockEntirely()
    {
        ServerClock.Observe(DateTimeOffset.UtcNow.AddSeconds(20), TimeSpan.Zero);
        var started = new DateTime(2026, 9, 6, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal("2m 30s", PipelineRunFormatting.FormatDuration(started, started.AddSeconds(150)));
    }
}
