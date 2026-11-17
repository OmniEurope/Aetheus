// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Tests.AppMonitoring;

/// <summary>R-13/R-23: the rule that keeps a zero from being shown when nothing fresh was ingested.</summary>
public sealed class OnlineAudienceTests
{
    private static readonly DateTime WindowStart = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Resolve_AnalyticsOff_IsUnavailable_WhateverElseIsKnown()
    {
        Assert.Equal(OnlineAudienceState.Unavailable,
            OnlineAudience.Resolve(false, 4, WindowStart.AddMinutes(1), WindowStart));
    }

    [Fact]
    public void Resolve_NothingEverIngested_IsWaiting()
    {
        Assert.Equal(OnlineAudienceState.Waiting, OnlineAudience.Resolve(true, 0, null, WindowStart));
    }

    [Fact]
    public void Resolve_LastIngestionOlderThanTheWindow_IsWaiting()
    {
        Assert.Equal(OnlineAudienceState.Waiting,
            OnlineAudience.Resolve(true, 0, WindowStart.AddSeconds(-1), WindowStart));
    }

    [Fact]
    public void Resolve_IngestionInsideTheWindow_WithNoVisitor_IsAMeasuredZero()
    {
        Assert.Equal(OnlineAudienceState.Measured, OnlineAudience.Resolve(true, 0, WindowStart, WindowStart));
    }

    [Fact]
    public void Resolve_VisitorsPresent_AreMeasured_EvenWithoutAnIngestionTime()
    {
        Assert.Equal(OnlineAudienceState.Measured, OnlineAudience.Resolve(true, 2, null, WindowStart));
    }

    [Fact]
    public void Combine_IsMeasuredAsSoonAsOneIs_WaitingWhenEveryEnabledOneWaits_ElseUnavailable()
    {
        Assert.Equal(OnlineAudienceState.Measured,
            OnlineAudience.Combine([OnlineAudienceState.Waiting, OnlineAudienceState.Measured, OnlineAudienceState.Unavailable]));
        Assert.Equal(OnlineAudienceState.Waiting,
            OnlineAudience.Combine([OnlineAudienceState.Unavailable, OnlineAudienceState.Waiting]));
        Assert.Equal(OnlineAudienceState.Unavailable, OnlineAudience.Combine([]));
        Assert.Equal(OnlineAudienceState.Unavailable, OnlineAudience.Combine([OnlineAudienceState.Unavailable]));
    }
}
