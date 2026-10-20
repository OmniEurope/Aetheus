// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests.Monitoring;

/// <summary>
/// PLAN-010 lot 1, clock drift: the NTP estimate built from one heartbeat exchange, and the alert
/// that reads it. Two fake clocks stand for the agent's and the backend's, set apart by a known
/// offset, so every expected figure is computed from the scenario rather than restated.
/// </summary>
public sealed class ClockOffsetEstimateTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One exchange: the request travels <paramref name="upMs"/>, the backend works
    /// <paramref name="processingMs"/>, the reply travels <paramref name="downMs"/>. The agent's clock
    /// reads <paramref name="agentAheadMs"/> more than the backend's throughout.
    /// </summary>
    private static ClockSampleDto Exchange(double agentAheadMs, double upMs, double processingMs, double downMs)
    {
        var backend = new FakeTimeProvider(Start);
        var agent = new FakeTimeProvider(Start.AddMilliseconds(agentAheadMs));

        var sentAt = agent.GetUtcNow().UtcDateTime;
        var sendStarted = agent.GetTimestamp();
        Advance(TimeSpan.FromMilliseconds(upMs));
        var receivedAt = backend.GetUtcNow().UtcDateTime;
        Advance(TimeSpan.FromMilliseconds(processingMs));
        var repliedAt = backend.GetUtcNow().UtcDateTime;
        Advance(TimeSpan.FromMilliseconds(downMs));

        return ClockSampleDto.FromExchange(sentAt, agent.GetElapsedTime(sendStarted), receivedAt, repliedAt)!;

        void Advance(TimeSpan delta)
        {
            backend.Advance(delta);
            agent.Advance(delta);
        }
    }

    [Fact]
    public void SymmetricPaths_GiveTheExactOffset_AndHalfTheNetworkRoundTripAsBound()
    {
        var estimate = Exchange(agentAheadMs: 3_000, upMs: 40, processingMs: 500, downMs: 40).Estimate();

        Assert.NotNull(estimate);
        Assert.Equal(3_000, estimate.Value.OffsetMs);
        // The 500 ms of processing is not network delay: only 40 + 40 counts.
        Assert.Equal(40, estimate.Value.UncertaintyMs);
    }

    [Fact]
    public void AgentBehind_GivesANegativeOffset()
    {
        var estimate = Exchange(agentAheadMs: -12_500, upMs: 20, processingMs: 5, downMs: 20).Estimate();

        Assert.Equal(-12_500, estimate!.Value.OffsetMs);
    }

    [Theory]
    [InlineData(0, 300, 0)]
    [InlineData(0, 0, 300)]
    [InlineData(2_000, 250, 10)]
    [InlineData(-2_000, 10, 250)]
    public void AsymmetricPaths_StayWithinTheBound(double agentAheadMs, double upMs, double downMs)
    {
        var estimate = Exchange(agentAheadMs, upMs, processingMs: 100, downMs).Estimate()!.Value;

        // The estimate errs by half the asymmetry, never by more than half the round trip.
        Assert.Equal((upMs + downMs) / 2, estimate.UncertaintyMs);
        Assert.InRange(agentAheadMs, estimate.OffsetMs - estimate.UncertaintyMs, estimate.OffsetMs + estimate.UncertaintyMs);
        Assert.Equal(agentAheadMs + (downMs - upMs) / 2, estimate.OffsetMs);
    }

    [Fact]
    public void RoundTripLongerThanTheLimit_IsDiscarded()
    {
        var sample = Exchange(agentAheadMs: 5_000, upMs: 6_000, processingMs: 0, downMs: 6_000);

        Assert.Null(sample.Estimate());
    }

    [Fact]
    public void InconsistentSample_IsDiscarded()
    {
        var sample = Exchange(agentAheadMs: 0, upMs: 10, processingMs: 10, downMs: 10) with
        {
            // A reply stamped before its request was received.
            BackendRepliedAtUtc = Start.UtcDateTime.AddSeconds(-1)
        };

        Assert.Null(sample.Estimate());
    }

    [Fact]
    public void UnstampedReply_GivesNoSample()
    {
        Assert.Null(ClockSampleDto.FromExchange(Start.UtcDateTime, TimeSpan.FromMilliseconds(50), null, null));
    }

    [Fact]
    public void Heartbeat_WithoutSample_StoresAnUnknownOffset_NotZero()
    {
        var metric = ServerMetricDto.FromHeartbeat(new ServerHeartbeatDto(), 1, Start.UtcDateTime);

        Assert.Null(metric.ClockOffsetMs);
        Assert.Null(metric.ClockOffsetUncertaintyMs);
    }

    [Fact]
    public void Heartbeat_WithSample_StoresItsEstimate()
    {
        var heartbeat = new ServerHeartbeatDto
        {
            ClockSample = Exchange(agentAheadMs: 1_500, upMs: 30, processingMs: 200, downMs: 30)
        };

        var metric = ServerMetricDto.FromHeartbeat(heartbeat, 1, Start.UtcDateTime);

        Assert.Equal(1_500, metric.ClockOffsetMs);
        Assert.Equal(30, metric.ClockOffsetUncertaintyMs);
    }

    private static AlertRule ClockRule(double thresholdSeconds) => new()
    {
        Id = 1,
        Name = "clock",
        Metric = MetricType.ClockOffset,
        Operator = ComparisonOperator.GreaterThanOrEqual,
        Threshold = thresholdSeconds,
        SustainedSeconds = 300
    };

    private static ServerMetric MetricWith(double? offsetMs, double? uncertaintyMs) => new()
    {
        ClockOffsetMs = offsetMs,
        ClockOffsetUncertaintyMs = uncertaintyMs,
        Timestamp = Start.UtcDateTime
    };

    [Theory]
    [InlineData(1_500, 100, true)]    // at least 1.4 s guaranteed
    [InlineData(-1_500, 100, true)]   // behind counts as much as ahead
    [InlineData(1_050, 100, false)]   // could be 0.95 s: not guaranteed
    [InlineData(900, 20, false)]
    public void Alert_ReadsTheOffsetTheMeasureGuarantees(double offsetMs, double uncertaintyMs, bool breached)
    {
        Assert.Equal(breached, AlertEvaluatorService.IsThresholdBreached(ClockRule(1), MetricWith(offsetMs, uncertaintyMs)));
    }

    [Fact]
    public void Alert_StaysSilent_WhenTheOffsetIsUnknown()
    {
        Assert.False(AlertEvaluatorService.IsThresholdBreached(ClockRule(0), MetricWith(null, null)));
    }
}
