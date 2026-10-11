// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.DailyRecap;

/// <summary>The daily recap e-mail: its settings, its preview, and a send on request.</summary>
public interface IDailyRecapService
{
    Task<DailyRecapSettingsDto> GetSettingsAsync(CancellationToken ct = default);
    Task<DailyRecapSettingsDto> UpdateSettingsAsync(UpdateDailyRecapSettingsRequest request, CancellationToken ct = default);

    /// <summary>The recap of yesterday as it would be sent now, with the saved settings.</summary>
    Task<DailyRecapPreviewDto> PreviewAsync(CancellationToken ct = default);

    /// <summary>Sends yesterday's recap now to the recipients of the next recap, and reports what the SMTP server answered.</summary>
    Task<DailyRecapSendResultDto> SendNowAsync(CancellationToken ct = default);
}
