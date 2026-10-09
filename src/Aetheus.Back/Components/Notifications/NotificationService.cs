// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aetheus.Back.Components.Notifications.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Notifications;

public class NotificationService(
    INotificationRepository repo,
    IAuditService audit,
    // No AI trigger port: this module states that an event happened and subscribers decide what it
    // means. Injecting the AI module to start its own triggers is what put the two in a cycle.
    IDomainEventDispatcher domainEvents,
    IHttpClientFactory httpClientFactory,
    ILogger<NotificationService> logger,
    IEncryptionService encryption,
    TimeProvider timeProvider,
    IUserNotificationService userNotifications) : INotificationService
{
    public async Task<PaginatedResult<NotificationChannelDto>> GetChannelsAsync(
        PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (channels, total) = await repo.GetChannelsPagedAsync(
            request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<NotificationChannelDto>
        {
            Items = channels.Select(MapChannelToDto).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<NotificationChannelDto?> GetChannelAsync(int id, CancellationToken ct = default)
    {
        var channel = await repo.GetChannelWithRulesAsync(id, ct).ConfigureAwait(false);
        return channel is null ? null : MapChannelToDto(channel);
    }

    public async Task<NotificationChannelDto> CreateChannelAsync(CreateNotificationChannelRequest request, CancellationToken ct = default)
    {
        var channel = new NotificationChannel
        {
            Name = request.Name,
            Type = request.Type,
            ConfigurationJson = NotificationConfigurationProtection.Protect(
                encryption,
                request.ConfigurationJson)
        };
        await repo.AddChannelAsync(channel, ct).ConfigureAwait(false);
        await audit.LogAsync("Created", "NotificationChannel", channel.Id, channel.Name, ct).ConfigureAwait(false);
        return MapChannelToDto(channel);
    }

    public async Task<NotificationChannelDto?> UpdateChannelAsync(int id, UpdateNotificationChannelRequest request, CancellationToken ct = default)
    {
        var channel = await repo.FindChannelAsync(id, ct).ConfigureAwait(false);
        if (channel is null) return null;

        channel.Name = request.Name;
        var existingConfiguration = NotificationConfigurationProtection.Unprotect(
            encryption,
            channel.ConfigurationJson);
        var restoredConfiguration = SensitiveConfigurationJson.RestoreMaskedSecrets(
            existingConfiguration,
            request.ConfigurationJson);
        channel.ConfigurationJson = NotificationConfigurationProtection.Protect(
            encryption,
            restoredConfiguration);
        channel.IsEnabled = request.IsEnabled;
        channel.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.LogAsync("Updated", "NotificationChannel", channel.Id, channel.Name, ct).ConfigureAwait(false);
        return MapChannelToDto(channel);
    }

    public async Task<bool> DeleteChannelAsync(int id, CancellationToken ct = default)
    {
        var channel = await repo.FindChannelAsync(id, ct).ConfigureAwait(false);
        if (channel is null) return false;

        await repo.RemoveChannelAsync(channel, ct).ConfigureAwait(false);
        await audit.LogAsync("Deleted", "NotificationChannel", id, channel.Name, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<NotificationAdminFilterValuesDto> GetRuleFilterValuesAsync(CancellationToken ct = default)
    {
        var (eventTypes, channels) = await repo.GetRuleFilterValuesAsync(ct).ConfigureAwait(false);
        return new NotificationAdminFilterValuesDto { EventTypes = eventTypes, Channels = channels };
    }

    public async Task<PaginatedResult<NotificationRuleDto>> GetRulesAsync(
        PaginationRequest request, CancellationToken ct = default)
    {
        var (page, pageSize) = request.Normalize();
        var (rules, total) = await repo.GetRulesPagedAsync(
            request.Search, page, pageSize, request.SortBy, request.SortDescending, ct, request.Filters).ConfigureAwait(false);
        return new PaginatedResult<NotificationRuleDto>
        {
            Items = rules.Select(MapRuleToDto).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<NotificationTestResultDto?> TestChannelAsync(int id, CancellationToken ct = default)
    {
        var channel = await repo.FindChannelAsync(id, ct).ConfigureAwait(false);
        if (channel is null) return null;

        // Email has no real transport (SMTP is a stub): report NotConfigured, never "Sent".
        if (channel.Type == NotificationChannelType.Email)
        {
            return new NotificationTestResultDto
            {
                Status = NotificationTestStatus.NotConfigured,
                Message = "Email delivery is not configured (no SMTP transport); nothing was sent."
            };
        }

        var url = ExtractWebhookUrl(channel);
        if (string.IsNullOrWhiteSpace(url))
            return new NotificationTestResultDto { Status = NotificationTestStatus.NotConfigured, Message = "No webhook URL configured on this channel." };
        if (!await IsUrlSafeAsync(url, ct).ConfigureAwait(false))
            return new NotificationTestResultDto { Status = NotificationTestStatus.Failed, Message = "The configured URL is unreachable or blocked." };

        var testPayload = JsonSerializer.Serialize(new { test = true, message = "Aetheus notification channel test.", channel = channel.Name });
        try
        {
            await DispatchNotificationAsync(channel, "channel.test", testPayload, ct).ConfigureAwait(false);
            await audit.LogAsync("Tested", "NotificationChannel", channel.Id, channel.Name, ct).ConfigureAwait(false);
            return new NotificationTestResultDto { Status = NotificationTestStatus.Sent, Message = "Test message dispatched to the channel." };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // a caller-driven cancellation is not a channel "Failed" - propagate it
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Notification channel test failed for {Channel}", channel.Name);
            var message = ex is HttpRequestException { StatusCode: { } status }
                ? $"The provider rejected the message (HTTP {(int)status})."
                : "The provider could not be reached.";
            return new NotificationTestResultDto { Status = NotificationTestStatus.Failed, Message = message };
        }
    }

    private string? ExtractWebhookUrl(NotificationChannel channel)
    {
        try
        {
            var config = JsonSerializer.Deserialize<JsonElement>(GetConfiguration(channel));
            // Slack/Teams use "webhookUrl"; the generic Webhook channel uses "url".
            if (config.TryGetProperty("webhookUrl", out var w) && w.ValueKind == JsonValueKind.String) return w.GetString();
            if (config.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) return u.GetString();
        }
        catch (JsonException) { /* malformed config -> treated as no URL */ }
        return null;
    }

    public async Task<NotificationRuleDto> CreateRuleAsync(CreateNotificationRuleRequest request, CancellationToken ct = default)
    {
        var rule = new NotificationRule
        {
            NotificationChannelId = request.NotificationChannelId,
            EventType = request.EventType,
            FilterJson = request.FilterJson
        };
        await repo.AddRuleAsync(rule, ct).ConfigureAwait(false);
        return MapRuleToDto(rule);
    }

    public async Task<NotificationRuleDto?> UpdateRuleAsync(int id, UpdateNotificationRuleRequest request, CancellationToken ct = default)
    {
        var rule = await repo.FindRuleAsync(id, ct).ConfigureAwait(false);
        if (rule is null) return null;

        rule.EventType = request.EventType;
        rule.FilterJson = request.FilterJson;
        rule.IsEnabled = request.IsEnabled;
        await repo.SaveChangesAsync(ct).ConfigureAwait(false);
        return MapRuleToDto(rule);
    }

    public async Task<bool> DeleteRuleAsync(int id, CancellationToken ct = default)
    {
        var rule = await repo.FindRuleAsync(id, ct).ConfigureAwait(false);
        if (rule is null) return false;

        await repo.RemoveRuleAsync(rule, ct).ConfigureAwait(false);
        return true;
    }

    public async Task SendEventAsync(string eventType, object payload, CancellationToken ct = default)
    {
        try
        {
            // Observer dispatch, matching the catch below: a subscriber must never be able to stop a
            // notification from being delivered.
            await domainEvents
                .DispatchAsync(new NotificationEventRaisedEvent(eventType, payload), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "AI trigger dispatch failed for event {EventType}; normal notification delivery continues",
                eventType);
        }
        var jsonPayload = JsonSerializer.Serialize(payload);
        try
        {
            // Per-user record of a project event, for the project's subscribers. Observer semantics like
            // the dispatch above: a failure here must not stop the admin channels below.
            await userNotifications.RecordProjectEventAsync(eventType, jsonPayload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Recording user notifications failed for event {EventType}; channel delivery continues",
                eventType);
        }

        var rules = await repo.GetRulesForEventAsync(eventType, ct).ConfigureAwait(false);
        if (rules.Count == 0) return;

        foreach (var rule in rules)
        {
            try
            {
                await DispatchNotificationAsync(rule.Channel, eventType, jsonPayload, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send notification for event {EventType} to channel {ChannelName}",
                    eventType, rule.Channel.Name);
            }
        }
    }

    private async Task DispatchNotificationAsync(NotificationChannel channel, string eventType, string jsonPayload, CancellationToken ct)
    {
        switch (channel.Type)
        {
            case NotificationChannelType.Email:
                logger.LogInformation("Email notification for {EventType} - channel {Channel} (SMTP not configured)", eventType, channel.Name);
                break;

            case NotificationChannelType.Slack:
                await SendSlackNotificationAsync(channel, eventType, jsonPayload, ct).ConfigureAwait(false);
                break;

            case NotificationChannelType.Teams:
                await SendTeamsNotificationAsync(channel, eventType, jsonPayload, ct).ConfigureAwait(false);
                break;

            case NotificationChannelType.Webhook:
                await SendWebhookNotificationAsync(channel, eventType, jsonPayload, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task SendSlackNotificationAsync(NotificationChannel channel, string eventType, string jsonPayload, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<JsonElement>(GetConfiguration(channel));
        if (!config.TryGetProperty("webhookUrl", out var urlElement)) return;
        if (!await IsUrlSafeAsync(urlElement.GetString(), ct).ConfigureAwait(false)) return;

        var slackPayload = JsonSerializer.Serialize(new { text = $"*[Aetheus]* {eventType}\n```{jsonPayload}```" });
        using var client = httpClientFactory.CreateClient("webhooks");
        using var content = new StringContent(slackPayload, Encoding.UTF8, "application/json");
        // A provider that answers 401, 429 or 500 did not take the message: the status is the verdict.
        using var response = await client.PostAsync(urlElement.GetString(), content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task SendTeamsNotificationAsync(NotificationChannel channel, string eventType, string jsonPayload, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<JsonElement>(GetConfiguration(channel));
        if (!config.TryGetProperty("webhookUrl", out var urlElement)) return;
        if (!await IsUrlSafeAsync(urlElement.GetString(), ct).ConfigureAwait(false)) return;

        var teamsPayload = JsonSerializer.Serialize(new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = new object[]
                        {
                            new { type = "TextBlock", text = $"Aetheus - {eventType}", weight = "Bolder", size = "Medium" },
                            new { type = "TextBlock", text = jsonPayload, wrap = true, isSubtle = true }
                        }
                    }
                }
            }
        });

        using var client = httpClientFactory.CreateClient("webhooks");
        using var content = new StringContent(teamsPayload, Encoding.UTF8, "application/json");
        // A provider that answers 401, 429 or 500 did not take the message: the status is the verdict.
        using var response = await client.PostAsync(urlElement.GetString(), content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task SendWebhookNotificationAsync(NotificationChannel channel, string eventType, string jsonPayload, CancellationToken ct)
    {
        var config = JsonSerializer.Deserialize<JsonElement>(GetConfiguration(channel));
        if (!config.TryGetProperty("url", out var urlElement)) return;
        if (!await IsUrlSafeAsync(urlElement.GetString(), ct).ConfigureAwait(false)) return;

        using var client = httpClientFactory.CreateClient("webhooks");
        using var request = new HttpRequestMessage(HttpMethod.Post, urlElement.GetString());
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        request.Headers.Add("X-Aetheus-Event", eventType);

        if (config.TryGetProperty("secret", out var secretElement))
        {
            var signature = WebhookSignatureValidator.Compute(secretElement.GetString()!, jsonPayload);
            request.Headers.Add("X-Aetheus-Signature", $"sha256={signature}");
        }

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<bool> IsUrlSafeAsync(string? targetUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetUrl) || !Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("http" or "https"))
            return false;
        if (IPAddress.TryParse(uri.Host, out var literal))
            return !WebhookSsrfGuard.IsForbiddenAddress(literal);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
            return addresses.Length > 0 && !addresses.Any(WebhookSsrfGuard.IsForbiddenAddress);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    private NotificationChannelDto MapChannelToDto(NotificationChannel c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Type = c.Type,
        ConfigurationJson = SensitiveConfigurationJson.MaskSecrets(GetConfiguration(c)),
        IsEnabled = c.IsEnabled,
        RuleCount = c.Rules.Count,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt
    };

    private string GetConfiguration(NotificationChannel channel) =>
        NotificationConfigurationProtection.Unprotect(encryption, channel.ConfigurationJson);

    private static NotificationRuleDto MapRuleToDto(NotificationRule r) => new()
    {
        Id = r.Id,
        NotificationChannelId = r.NotificationChannelId,
        ChannelName = r.Channel?.Name ?? string.Empty,
        EventType = r.EventType,
        FilterJson = r.FilterJson,
        IsEnabled = r.IsEnabled,
        CreatedAt = r.CreatedAt
    };
}
