// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.DailyRecap;

/// <summary>Why the scheduler sends the recap now, or does not.</summary>
internal enum DailyRecapScheduleOutcome
{
    /// <summary>The send time of today has passed and today has not been sent: send, once.</summary>
    Due,

    /// <summary>The recap is switched off.</summary>
    Disabled,

    /// <summary>Today's send time has not come yet.</summary>
    NotYet,

    /// <summary>Today was already sent (or claimed by another instance).</summary>
    AlreadySent
}

/// <summary>The scheduler's verdict: <see cref="Today"/> is the local date the send is claimed under, and
/// <see cref="CoveredDay"/> the previous calendar day the recap tells about.</summary>
internal sealed record DailyRecapScheduleDecision(DailyRecapScheduleOutcome Outcome, DateOnly Today)
{
    public DateOnly CoveredDay => Today.AddDays(-1);
}

