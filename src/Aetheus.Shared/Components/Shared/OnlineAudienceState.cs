// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Shared;

/// <summary>What the "online" counter of an application or a project can honestly say (R-13, R-23). The
/// counter is the number of distinct telemetry visitors seen in the last five minutes; this state says
/// whether that number was measured at all, so a zero is never shown when nothing is known.</summary>
public enum OnlineAudienceState
{
    /// <summary>Web analytics is not enabled: there is no counter to show.</summary>
    Unavailable = 0,

    /// <summary>Web analytics is enabled but nothing fresh reached Aetheus: no event was ever ingested or
    /// the last one is older than the window. The count is not a measured zero.</summary>
    Waiting = 1,

    /// <summary>The count was measured: visitors are present, or events arrived within the window and
    /// none of them is a live visitor (a real zero).</summary>
    Measured = 2
}

/// <summary>The rule that turns an active-visitor count and the last ingestion time of one application
/// into an <see cref="OnlineAudienceState"/>.</summary>
public static class OnlineAudience
{
    /// <summary>The window of the "online" counter, in minutes (distinct visitors seen in it).</summary>
    public const int WindowMinutes = 5;

    /// <summary>Visitors seen in the window, or an ingestion inside the window, mean a measured count; an
    /// enabled application whose last ingestion is missing or older than the window and that shows no
    /// visitor is waiting for data.</summary>
    public static OnlineAudienceState Resolve(bool analyticsEnabled, int activeVisitors, DateTime? lastIngestUtc, DateTime windowStartUtc)
    {
        if (!analyticsEnabled) return OnlineAudienceState.Unavailable;
        return activeVisitors > 0 || lastIngestUtc >= windowStartUtc
            ? OnlineAudienceState.Measured
            : OnlineAudienceState.Waiting;
    }

    /// <summary>The state of several applications counted together: measured as soon as one is, waiting
    /// when every enabled one is, unavailable when none measures its audience.</summary>
    public static OnlineAudienceState Combine(IEnumerable<OnlineAudienceState> states)
    {
        var combined = OnlineAudienceState.Unavailable;
        foreach (var state in states)
        {
            if (state == OnlineAudienceState.Measured) return state;
            if (state == OnlineAudienceState.Waiting) combined = state;
        }
        return combined;
    }
}
