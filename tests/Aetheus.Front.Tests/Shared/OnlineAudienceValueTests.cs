// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Shared;

/// <summary>R-23: the online counter tells four situations apart and never shows a bare zero for missing data:
/// a fresh count, a fresh zero, stale data (waiting, since the last event) and no data ever received.</summary>
public sealed class OnlineAudienceValueTests : BunitContext
{
    private static readonly DateTime LastEvent = new(2026, 11, 5, 10, 38, 0, DateTimeKind.Utc);

    private IRenderedComponent<OnlineAudienceValue> RenderCounter(
        OnlineAudienceState state, int? count, DateTime? lastEventAt = null)
    {
        BunitTestHelper.RegisterServices(this);
        return Render<OnlineAudienceValue>(parameters => parameters
            .Add(c => c.State, state)
            .Add(c => c.Count, count)
            .Add(c => c.LastEventAt, lastEventAt));
    }

    [Fact]
    public void FreshWithVisitors_ShowsTheCount_NotTheWaitingWords()
    {
        var cut = RenderCounter(OnlineAudienceState.Measured, 3, LastEvent);

        Assert.Equal("3", cut.Find("[data-testid='online-audience-count']").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='online-audience-waiting']"));
        Assert.DoesNotContain("OnlineUsersWaiting", cut.Find("[data-testid='online-audience-help']").GetAttribute("aria-label"), StringComparison.Ordinal);
    }

    [Fact]
    public void FreshWithNobody_ShowsAMeasuredZero()
    {
        var cut = RenderCounter(OnlineAudienceState.Measured, 0, LastEvent);

        Assert.Equal("0", cut.Find("[data-testid='online-audience-count']").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='online-audience-waiting']"));
    }

    [Fact]
    public void StaleData_SaysWaiting_WithTheLastDataTime_AndNoNumber()
    {
        var cut = RenderCounter(OnlineAudienceState.Waiting, 0, LastEvent);

        Assert.NotNull(cut.Find("[data-testid='online-audience-waiting']"));
        Assert.Empty(cut.FindAll("[data-testid='online-audience-count']"));
        var help = cut.Find("[data-testid='online-audience-help']").GetAttribute("aria-label");
        Assert.Contains("OnlineUsersWaitingLast", help, StringComparison.Ordinal);
        Assert.DoesNotContain("OnlineUsersWaitingNever", help, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDataEverReceived_SaysWaiting_AndThatNothingArrivedYet()
    {
        var cut = RenderCounter(OnlineAudienceState.Waiting, 0);

        Assert.NotNull(cut.Find("[data-testid='online-audience-waiting']"));
        Assert.Empty(cut.FindAll("[data-testid='online-audience-count']"));
        var help = cut.Find("[data-testid='online-audience-help']").GetAttribute("aria-label");
        Assert.Contains("OnlineUsersWaitingNever", help, StringComparison.Ordinal);
    }

    [Fact]
    public void Unavailable_DrawsNothing()
    {
        var cut = RenderCounter(OnlineAudienceState.Unavailable, null);

        Assert.Empty(cut.FindAll("[data-testid='online-audience']"));
    }

    [Fact]
    public void TheWaitingHint_CarriesTheLocalTimeOfTheLastData_OrSaysNothingArrived()
    {
        var localizer = new FormatLocalizer();

        var since = OnlineAudienceText.WaitingHint(localizer, LastEvent);
        var never = OnlineAudienceText.WaitingHint(localizer, null);

        Assert.Contains($"last:{LastEvent.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)}", since, StringComparison.Ordinal);
        Assert.Contains("hint", since, StringComparison.Ordinal);
        Assert.Contains("never", never, StringComparison.Ordinal);
        Assert.DoesNotContain("last:", never, StringComparison.Ordinal);
    }

    private sealed class FormatLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name switch
        {
            "OnlineUsersWaitingHint" => "hint",
            "OnlineUsersWaitingNever" => "never",
            _ => name
        });

        public LocalizedString this[string name, params object[] arguments] => new(name, name == "OnlineUsersWaitingLast"
            ? $"last:{arguments[0]}"
            : name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
