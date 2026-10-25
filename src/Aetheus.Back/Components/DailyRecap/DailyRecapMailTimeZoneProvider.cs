// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// The daily recap's time zone setting, read as the recap service reads it. The settings page refuses an
/// unknown zone; a host without timezone data falls back to UTC, and says so.
/// </summary>
internal sealed class DailyRecapMailTimeZoneProvider(
    IDailyRecapRepository repo,
    ILogger<DailyRecapMailTimeZoneProvider> logger)
{
    public async Task<TimeZoneInfo> GetAsync(CancellationToken ct = default)
    {
        var settings = DailyRecapSettings.Parse(await repo.GetSettingRowsAsync(ct).ConfigureAwait(false));
        if (DailyRecapClock.TryResolveTimeZone(settings.TimeZoneId, out var timeZone)) return timeZone;
        logger.LogWarning("[DailyRecap] timezone {TimeZone} is unknown on this host; UTC is used", settings.TimeZoneId);
        return TimeZoneInfo.Utc;
    }
}
