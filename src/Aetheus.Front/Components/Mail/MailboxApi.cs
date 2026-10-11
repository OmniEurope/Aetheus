// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Aetheus.Front.Components.Mail;

/// <summary>
/// The answer of a mailbox call: the value on success, the status otherwise. A 409 means the backend no
/// longer holds the mailbox password (never typed, locked, expired or refused by the server): the page
/// asks for it again.
/// </summary>
public readonly record struct MailboxResult<T>(T? Value, HttpStatusCode Status)
{
    public bool Ok => (int)Status is >= 200 and < 300;
    public bool Locked => Status == HttpStatusCode.Conflict;
}

/// <summary>Recette R1-17: the integrated mailbox of a mail account (IMAP and SMTP through the backend).</summary>
public sealed class MailboxApi(HttpClient http) : ApiClientBase(http)
{
    private static string Base(int serverId, int accountId) => $"api/servers/{serverId}/mail/accounts/{accountId}/mailbox";

    public Task<MailboxResult<MailClientSettingsDto>> GetClientSettingsAsync(int serverId, CancellationToken ct = default) =>
        SendAsync<MailClientSettingsDto>(HttpMethod.Get, $"api/servers/{serverId}/mail/client-settings", null, ct);

    public Task<MailboxResult<MailboxAccessDto>> GetAccessAsync(int serverId, int accountId, CancellationToken ct = default) =>
        SendAsync<MailboxAccessDto>(HttpMethod.Get, Base(serverId, accountId), null, ct);

    public Task<MailboxResult<MailboxAccessDto>> UnlockAsync(int serverId, int accountId, MailboxUnlockRequest request, CancellationToken ct = default) =>
        SendAsync<MailboxAccessDto>(HttpMethod.Post, $"{Base(serverId, accountId)}/unlock", request, ct);

    public async Task<ApiStatus> LockAsync(int serverId, int accountId, CancellationToken ct = default) =>
        await PostNoBodyAsync($"{Base(serverId, accountId)}/lock", ct).ConfigureAwait(false);

    public Task<MailboxResult<List<MailboxFolderDto>>> GetFoldersAsync(int serverId, int accountId, CancellationToken ct = default) =>
        SendAsync<List<MailboxFolderDto>>(HttpMethod.Get, $"{Base(serverId, accountId)}/folders", null, ct);

    public Task<MailboxResult<MailboxFolderDto>> CreateFolderAsync(int serverId, int accountId, MailboxCreateFolderRequest request, CancellationToken ct = default) =>
        SendAsync<MailboxFolderDto>(HttpMethod.Post, $"{Base(serverId, accountId)}/folders", request, ct);

    public Task<MailboxResult<PaginatedResult<MailboxMessageSummaryDto>>> GetMessagesAsync(
        int serverId, int accountId, string folder, int page, int pageSize, CancellationToken ct = default) =>
        SendAsync<PaginatedResult<MailboxMessageSummaryDto>>(HttpMethod.Get, QueryHelpers.AddQueryString(
            $"{Base(serverId, accountId)}/messages",
            new Dictionary<string, string?> { ["folder"] = folder, ["page"] = page.ToString(CultureInfo.InvariantCulture), ["pageSize"] = pageSize.ToString(CultureInfo.InvariantCulture) }),
            null, ct);

    public Task<MailboxResult<MailboxMessageDto>> GetMessageAsync(
        int serverId, int accountId, string folder, uint uid, bool loadImages, CancellationToken ct = default) =>
        SendAsync<MailboxMessageDto>(HttpMethod.Get, QueryHelpers.AddQueryString(
            $"{Base(serverId, accountId)}/messages/{uid}",
            new Dictionary<string, string?> { ["folder"] = folder, ["loadImages"] = loadImages ? "true" : "false" }),
            null, ct);

    public Task<MailboxResult<MailboxDraftDto>> GetDraftAsync(
        int serverId, int accountId, string folder, uint uid, MailboxComposeMode mode, string language, CancellationToken ct = default) =>
        SendAsync<MailboxDraftDto>(HttpMethod.Get, QueryHelpers.AddQueryString(
            $"{Base(serverId, accountId)}/messages/{uid}/draft",
            new Dictionary<string, string?> { ["folder"] = folder, ["mode"] = mode.ToString(), ["language"] = language }),
            null, ct);

    public Task<MailboxResult<bool>> SetReadAsync(
        int serverId, int accountId, string folder, uint uid, bool isRead, CancellationToken ct = default) =>
        SendAsync<bool>(HttpMethod.Put, QueryHelpers.AddQueryString(
            $"{Base(serverId, accountId)}/messages/{uid}/read", "folder", folder),
            new MailboxReadStateRequest { IsRead = isRead }, ct);

    public Task<MailboxResult<MailboxDeleteResultDto>> DeleteAsync(
        int serverId, int accountId, string folder, uint uid, CancellationToken ct = default) =>
        SendAsync<MailboxDeleteResultDto>(HttpMethod.Delete, QueryHelpers.AddQueryString(
            $"{Base(serverId, accountId)}/messages/{uid}", "folder", folder), null, ct);

    public Task<MailboxResult<MailboxSendResultDto>> SendAsync(
        int serverId, int accountId, MailboxSendRequest request, CancellationToken ct = default) =>
        SendAsync<MailboxSendResultDto>(HttpMethod.Post, $"{Base(serverId, accountId)}/send", request, ct);

    /// <summary>The attachment's bytes, or null when the call failed (the error is already shown).</summary>
    public async Task<byte[]?> GetAttachmentAsync(
        int serverId, int accountId, string folder, uint uid, int index, CancellationToken ct = default)
    {
        var url = QueryHelpers.AddQueryString($"{Base(serverId, accountId)}/messages/{uid}/attachments/{index}", "folder", folder);
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false) : null;
    }

    private async Task<MailboxResult<T>> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return new MailboxResult<T>(default, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(bool))
            return new MailboxResult<T>(default, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions.Web, ct).ConfigureAwait(false);
        return new MailboxResult<T>(value, response.StatusCode);
    }
}
