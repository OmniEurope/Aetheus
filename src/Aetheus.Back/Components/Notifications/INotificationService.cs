// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Notifications;

public interface INotificationService
{
    Task<PaginatedResult<NotificationChannelDto>> GetChannelsAsync(
        PaginationRequest request, CancellationToken ct = default);
    Task<NotificationChannelDto?> GetChannelAsync(int id, CancellationToken ct = default);
    Task<NotificationChannelDto> CreateChannelAsync(CreateNotificationChannelRequest request, CancellationToken ct = default);
    Task<NotificationChannelDto?> UpdateChannelAsync(int id, UpdateNotificationChannelRequest request, CancellationToken ct = default);
    Task<bool> DeleteChannelAsync(int id, CancellationToken ct = default);
    /// <summary>Sends a real test message; an E-mail channel writes to <paramref name="requestingUserId"/>'s account address.</summary>
    Task<NotificationTestResultDto?> TestChannelAsync(int id, int? requestingUserId, CancellationToken ct = default);
    Task<PaginatedResult<NotificationRuleDto>> GetRulesAsync(
        PaginationRequest request, CancellationToken ct = default);

    /// <summary>Recette R-224: the values the rules grid's checkable column filters offer.</summary>
    Task<NotificationAdminFilterValuesDto> GetRuleFilterValuesAsync(CancellationToken ct = default);
    Task<NotificationRuleDto> CreateRuleAsync(CreateNotificationRuleRequest request, CancellationToken ct = default);
    Task<NotificationRuleDto?> UpdateRuleAsync(int id, UpdateNotificationRuleRequest request, CancellationToken ct = default);
    Task<bool> DeleteRuleAsync(int id, CancellationToken ct = default);
    Task SendEventAsync(string eventType, object payload, CancellationToken ct = default);
}
