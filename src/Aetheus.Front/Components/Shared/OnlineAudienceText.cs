// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>The sentence that goes with an « en attente de données » online counter (R-23): it says the
/// zero is not measured and, when Aetheus knows it, when the last audience data arrived, so the wait has a
/// starting point instead of an open-ended "waiting".</summary>
public static class OnlineAudienceText
{
    /// <summary>« Aucun événement d'audience reçu depuis moins de 5 minutes ... » followed by the last data
    /// received (local time) or by the statement that nothing was ever received.</summary>
    public static string WaitingHint(IStringLocalizer localizer, DateTime? lastEventAtUtc)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var since = lastEventAtUtc is { } at
            ? localizer["OnlineUsersWaitingLast", DateTime.SpecifyKind(at, DateTimeKind.Utc).ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)].Value
            : localizer["OnlineUsersWaitingNever"].Value;
        return $"{localizer["OnlineUsersWaitingHint"].Value} {since}";
    }
}
