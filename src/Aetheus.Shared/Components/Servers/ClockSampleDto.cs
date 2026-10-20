// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Servers;

/// <summary>
/// The four instants of one heartbeat exchange, as NTP reads them (PLAN-010 lot 1, clock drift):
/// the agent's clock when it sent the heartbeat (T1) and when the reply came back (T4), the
/// backend's clock when it received it (T2) and when it replied (T3). The agent reports the sample
/// of its previous exchange in the next heartbeat, since T4 only exists once the reply is in.
/// </summary>
public sealed record ClockSampleDto
{
    /// <summary>T1, agent clock.</summary>
    public DateTime AgentSentAtUtc { get; init; }
    /// <summary>T2, backend clock.</summary>
    public DateTime BackendReceivedAtUtc { get; init; }
    /// <summary>T3, backend clock.</summary>
    public DateTime BackendRepliedAtUtc { get; init; }
    /// <summary>T4, agent clock: T1 plus the elapsed time read on the agent's monotonic clock, so a
    /// wall-clock step during the exchange cannot shorten or lengthen the round trip.</summary>
    public DateTime AgentReceivedAtUtc { get; init; }

    /// <summary>A round trip longer than this says nothing useful about the offset (the uncertainty
    /// is half of it) and is discarded rather than reported.</summary>
    public const double MaxRoundTripMs = 10_000;

    /// <summary>
    /// The sample of one exchange, or null when the backend did not stamp its reply (a backend
    /// predating the measure): no stamp, no sample, the offset stays unknown.
    /// </summary>
    public static ClockSampleDto? FromExchange(
        DateTime agentSentAtUtc, TimeSpan agentElapsed, DateTime? backendReceivedAtUtc, DateTime? backendRepliedAtUtc)
    {
        if (backendReceivedAtUtc is not { } received || backendRepliedAtUtc is not { } replied)
            return null;
        return new ClockSampleDto
        {
            AgentSentAtUtc = agentSentAtUtc,
            BackendReceivedAtUtc = received,
            BackendRepliedAtUtc = replied,
            AgentReceivedAtUtc = agentSentAtUtc + agentElapsed
        };
    }

    /// <summary>
    /// The NTP estimate of the agent's clock offset. Offset = ((T1 - T2) + (T4 - T3)) / 2, positive
    /// when the agent's clock is ahead of the backend's. Network delay = (T4 - T1) - (T3 - T2): the
    /// backend's own processing time is taken out, and whatever the split of the remaining delay
    /// between the two directions, the true offset lies within half of it of the estimate. That half
    /// is the uncertainty. Null when the sample is inconsistent (a negative interval) or the round
    /// trip exceeds <see cref="MaxRoundTripMs"/>.
    /// </summary>
    public ClockOffsetEstimate? Estimate()
    {
        var agentInterval = (AgentReceivedAtUtc - AgentSentAtUtc).TotalMilliseconds;
        var backendInterval = (BackendRepliedAtUtc - BackendReceivedAtUtc).TotalMilliseconds;
        if (agentInterval < 0 || backendInterval < 0) return null;
        var roundTrip = agentInterval - backendInterval;
        if (roundTrip is < 0 or > MaxRoundTripMs) return null;

        var offset = ((AgentSentAtUtc - BackendReceivedAtUtc).TotalMilliseconds
            + (AgentReceivedAtUtc - BackendRepliedAtUtc).TotalMilliseconds) / 2;
        return new ClockOffsetEstimate(Math.Round(offset, 1), Math.Round(roundTrip / 2, 1));
    }
}

/// <summary>An agent's clock offset against the backend's, in milliseconds, with its bound.</summary>
/// <param name="OffsetMs">Positive when the agent's clock is ahead of the backend's.</param>
/// <param name="UncertaintyMs">Half the network round trip: the true offset lies within it.</param>
public readonly record struct ClockOffsetEstimate(double OffsetMs, double UncertaintyMs)
{
    /// <summary>
    /// The smallest offset the measure guarantees, in milliseconds: zero when the uncertainty covers
    /// it. The alert reads this, so a slow network can widen the bound but never raise the alert.
    /// </summary>
    public double GuaranteedMs => Math.Max(0, Math.Abs(OffsetMs) - UncertaintyMs);
}
