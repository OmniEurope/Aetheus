// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>
/// Recette R1-42: after an idle stretch the confirmation of an agent self-update can be lost (SignalR
/// replays nothing for a sleeping tab). The card must not conclude a failure on its own: silence turns
/// into a check that re-reads the persisted request, and a persisted status change for the request
/// already followed is applied.
/// </summary>
public sealed class AgentUpdateProgressCardStaleTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public AgentUpdateProgressCardStaleTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static AgentUpdateRequestSummaryDto Request(AgentUpdateRequestStatus status, DateTime? handoffAt = null) => new()
    {
        RequestId = 4,
        ObservedVersion = "1.0.1547",
        TargetVersion = "1.0.1552",
        Status = status,
        HandoffAt = handoffAt
    };

    private IRenderedComponent<AgentUpdateProgressCard> RenderCard(
        AgentUpdateRequestSummaryDto request, Action? onStale = null) =>
        Render<AgentUpdateProgressCard>(p => p
            .Add(x => x.ServerId, 7)
            .Add(x => x.Request, request)
            .Add(x => x.OnStale, () => onStale?.Invoke()));

    private static AgentUpdatePhase? Phase(IRenderedComponent<AgentUpdateProgressCard> cut) =>
        (AgentUpdatePhase?)typeof(AgentUpdateProgressCard).GetField("_phase", Priv)!.GetValue(cut.Instance);

    [Fact]
    public void ASilentHandoff_ShowsACheck_NotAFailure_AndReportsNoClientError()
    {
        var stale = 0;
        var cut = RenderCard(Request(AgentUpdateRequestStatus.Handoff), () => stale++);

        cut.InvokeAsync(cut.Instance.OnStaleTimerElapsed);

        cut.WaitForAssertion(() => Assert.Contains("AgentUpdateCheckingHint", cut.Markup));
        Assert.Equal(AgentUpdatePhase.AgentOffline, Phase(cut));
        Assert.DoesNotContain("AgentUpdateFailed", cut.Markup);
        Assert.DoesNotContain(_handler.Requests, request => request.Url.Contains("client-errors", StringComparison.Ordinal));
        Assert.Equal(1, stale);
    }

    [Fact]
    public void AfterTheCheck_APersistedConfirmationOfTheSameRequest_EndsOnDone()
    {
        var cut = RenderCard(Request(AgentUpdateRequestStatus.Handoff));
        cut.InvokeAsync(cut.Instance.OnStaleTimerElapsed);

        cut.Render(p => p.Add(x => x.Request, Request(AgentUpdateRequestStatus.Confirmed)));

        Assert.Equal(AgentUpdatePhase.Done, Phase(cut));
        cut.WaitForAssertion(() => Assert.Contains("AgentUpdatePhase_Done", cut.Markup));
        Assert.DoesNotContain("AgentUpdateCheckingHint", cut.Markup);
    }

    [Fact]
    public void APersistedConfirmationOfTheSameRequest_ReplacesALocalFailure()
    {
        var cut = RenderCard(Request(AgentUpdateRequestStatus.Handoff));
        typeof(AgentUpdateProgressCard).GetField("_phase", Priv)!.SetValue(cut.Instance, AgentUpdatePhase.Failed);

        cut.Render(p => p.Add(x => x.Request, Request(AgentUpdateRequestStatus.Confirmed)));

        Assert.Equal(AgentUpdatePhase.Done, Phase(cut));
        cut.WaitForAssertion(() => Assert.DoesNotContain("AgentUpdateFailed", cut.Markup));
    }

    [Fact]
    public void APersistedFailureOfTheSameRequest_ShowsTheServersVerdict()
    {
        var cut = RenderCard(Request(AgentUpdateRequestStatus.Handoff));
        cut.InvokeAsync(cut.Instance.OnStaleTimerElapsed);

        cut.Render(p => p.Add(x => x.Request, Request(AgentUpdateRequestStatus.Failed) with
        {
            FailureDiagnostic = "No confirming heartbeat arrived before the deadline."
        }));

        Assert.Equal(AgentUpdatePhase.Failed, Phase(cut));
        Assert.Contains("No confirming heartbeat arrived before the deadline.", cut.Markup);
    }

    [Fact]
    public void AnUnchangedPersistedStatus_LeavesTheCardAsItIs()
    {
        var cut = RenderCard(Request(AgentUpdateRequestStatus.Handoff));
        typeof(AgentUpdateProgressCard).GetField("_phase", Priv)!.SetValue(cut.Instance, AgentUpdatePhase.Failed);

        cut.Render(p => p.Add(x => x.Request, Request(AgentUpdateRequestStatus.Handoff)));

        Assert.Equal(AgentUpdatePhase.Failed, Phase(cut));
    }

    [Fact]
    public void AnOlderPersistedStatus_DoesNotMoveTheCardBack()
    {
        var cut = RenderCard(Request(AgentUpdateRequestStatus.Downloading));
        typeof(AgentUpdateProgressCard).GetField("_phase", Priv)!.SetValue(cut.Instance, AgentUpdatePhase.LaunchingUpdater);

        cut.Render(p => p.Add(x => x.Request, Request(AgentUpdateRequestStatus.Staging)));

        Assert.Equal(AgentUpdatePhase.LaunchingUpdater, Phase(cut));
    }

    [Fact]
    public void OnceTheUpdaterIsLaunched_TheWaitFollowsTheBackendConfirmationWindow()
    {
        var cut = RenderCard(Request(AgentUpdateRequestStatus.Handoff, DateTime.Now.AddMinutes(-1)));

        var fromHandoff = cut.Instance.StaleSecondsFor(AgentUpdatePhase.AgentOffline);
        var whileDownloading = cut.Instance.StaleSecondsFor(AgentUpdatePhase.Downloading);

        // 5 min 15 s from a handoff one minute ago: about 255 s, far beyond the old fixed 90 s.
        Assert.InRange(fromHandoff, 250, 256);
        Assert.Equal(90, whileDownloading);
    }

    [Fact]
    public void WithoutAKnownHandoff_TheWaitIsTheWholeConfirmationWindow()
    {
        var cut = RenderCard(Request(AgentUpdateRequestStatus.Staging));

        var seconds = cut.Instance.StaleSecondsFor(AgentUpdatePhase.LaunchingUpdater);

        Assert.InRange(seconds, 310, 316);
    }
}
