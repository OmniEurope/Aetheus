// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Shared;

/// <summary>R1-42: the hidden time of a window is the sum of the hidden stretches inside it, open one included.</summary>
public sealed class PageVisibilityTimelineTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);

    private PageVisibilityTimeline Create() => new(_clock);

    [Fact]
    public void ANeverHiddenPage_HasNoHiddenTime()
    {
        var timeline = Create();
        _clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(TimeSpan.Zero, timeline.HiddenBetween(Start, _clock.GetUtcNow()));
    }

    [Fact]
    public void AClosedStretch_IsClippedToTheWindow()
    {
        var timeline = Create();
        _clock.Advance(TimeSpan.FromSeconds(10));
        timeline.Hidden();
        _clock.Advance(TimeSpan.FromSeconds(100));
        timeline.Visible();
        _clock.Advance(TimeSpan.FromSeconds(50));

        // The window starts 40 s into the stretch (at +50 s) and ends inside it as well (at +90 s).
        Assert.Equal(TimeSpan.FromSeconds(40), timeline.HiddenBetween(Start.AddSeconds(50), Start.AddSeconds(90)));
        // A window around the whole stretch gets exactly the stretch.
        Assert.Equal(TimeSpan.FromSeconds(100), timeline.HiddenBetween(Start, _clock.GetUtcNow()));
        // A window before it gets nothing.
        Assert.Equal(TimeSpan.Zero, timeline.HiddenBetween(Start, Start.AddSeconds(5)));
    }

    [Fact]
    public void AnOpenStretch_CountsUpToTheEndOfTheWindow()
    {
        var timeline = Create();
        _clock.Advance(TimeSpan.FromSeconds(30));
        timeline.Hidden();
        _clock.Advance(TimeSpan.FromSeconds(70));

        Assert.Equal(TimeSpan.FromSeconds(70), timeline.HiddenBetween(Start, _clock.GetUtcNow()));
        Assert.Equal(TimeSpan.FromSeconds(20), timeline.HiddenBetween(Start, Start.AddSeconds(50)));
    }

    [Fact]
    public void HiddenReportedTwice_KeepsTheFirstInstant()
    {
        var timeline = Create();
        timeline.Hidden();
        _clock.Advance(TimeSpan.FromSeconds(40));
        timeline.Hidden();
        _clock.Advance(TimeSpan.FromSeconds(60));
        timeline.Visible();

        Assert.Equal(TimeSpan.FromSeconds(100), timeline.HiddenBetween(Start, _clock.GetUtcNow()));
    }

    [Fact]
    public void VisibleWithoutAHiddenStretch_IsIgnored()
    {
        var timeline = Create();
        timeline.Visible();
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.Zero, timeline.HiddenBetween(Start, _clock.GetUtcNow()));
    }

    [Fact]
    public void AnEmptyOrReversedWindow_HasNoHiddenTime()
    {
        var timeline = Create();
        timeline.Hidden();
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.Zero, timeline.HiddenBetween(Start.AddSeconds(20), Start.AddSeconds(20)));
        Assert.Equal(TimeSpan.Zero, timeline.HiddenBetween(Start.AddSeconds(20), Start.AddSeconds(10)));
    }

    [Fact]
    public void OnlyTheLatestStretchesAreKept_SoTheMemoryStaysBounded()
    {
        var timeline = Create();
        for (var i = 0; i < PageVisibilityTimeline.MaxStretches + 10; i++)
        {
            timeline.Hidden();
            _clock.Advance(TimeSpan.FromSeconds(1));
            timeline.Visible();
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        // The first ten stretches (the first 20 s) were dropped; the retained ones are still counted.
        Assert.Equal(TimeSpan.Zero, timeline.HiddenBetween(Start, Start.AddSeconds(20)));
        Assert.Equal(
            TimeSpan.FromSeconds(PageVisibilityTimeline.MaxStretches),
            timeline.HiddenBetween(Start, _clock.GetUtcNow()));
    }
}
