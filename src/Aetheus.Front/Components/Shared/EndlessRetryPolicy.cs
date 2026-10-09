// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Decision of 2026-10-06: a realtime connection never gives up reconnecting. SignalR's default schedule
/// (0, 2, 10, 30 s) stops after four attempts and leaves the connection closed for good: a page left open
/// through a backend restart, a blue-green switch or a laptop asleep stayed frozen on what it last saw,
/// with no sign of it, until the user reloaded (the candidate run page, 2026-10-06). The delays are
/// <see cref="FrontendRuntimeDefaults.SignalRRetryDelays"/>, clamped to the last one once exhausted.
/// </summary>
internal sealed class EndlessRetryPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var delays = FrontendRuntimeDefaults.SignalRRetryDelays;
        return delays[(int)Math.Min(retryContext.PreviousRetryCount, delays.Count - 1)];
    }
}
