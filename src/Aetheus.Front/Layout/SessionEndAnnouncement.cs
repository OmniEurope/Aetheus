// SPDX-License-Identifier: EUPL-1.2
using Microsoft.Extensions.Logging;

namespace Aetheus.Front.Layout;

/// <summary>Announces a pending session-end reason once and logs it with a correlation id.
/// MainLayout calls it during startup and when sign-in is required.</summary>
internal static class SessionEndAnnouncement
{
    public static void Announce(
        AuthStateProvider auth, NotifyHelper notify, IStringLocalizer<AppStrings> localizer, ApiClient api, ILogger logger)
    {
        if (auth.TakeSessionEndReason() is not { } reason)
            return;

        var correlationId = Guid.NewGuid().ToString("N");
        notify.Notify(OmniSeverity.Warning, "SessionExpired",
            $"{localizer["SessionExpiredDetail"].Value} {localizer[$"SessionEnd_{reason}"].Value} ({correlationId[..8]})");
        _ = ReportAsync(api, logger, reason, correlationId);
    }

    private static async Task ReportAsync(ApiClient api, ILogger logger, string reason, string correlationId)
    {
        try
        {
            await api.Auth.ReportSessionEndedAsync(new SessionEndedReport { Reason = reason, CorrelationId = correlationId });
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not report the session end ({Reason})", reason);
        }
    }
}
