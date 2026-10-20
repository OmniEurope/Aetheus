// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>Use only the mailbox password held for the signed-in reader on each call. Missing
/// credentials or rejected mailbox authentication produce ConflictException, with rejected
/// credentials forgotten.</summary>
public interface IMailboxService
{
    Task<MailClientSettingsDto> GetClientSettingsAsync(int serverId, CancellationToken ct = default);

    /// <summary>
    /// Recette R1-41: one page of the active mailboxes of <paramref name="serverIds"/> (every server when null,
    /// none when empty), for the "Messagerie" page.
    /// </summary>
    Task<PaginatedResult<MailboxDirectoryEntryDto>> ListDirectoryAsync(
        IReadOnlyCollection<int>? serverIds, PaginationRequest request, CancellationToken ct = default);

    Task<MailboxAccessDto> GetAccessAsync(MailboxCredentialKey key, CancellationToken ct = default);

    /// <summary>Logs in with <paramref name="password"/>; only a password the server accepts is held.</summary>
    Task<MailboxAccessDto> UnlockAsync(MailboxCredentialKey key, string password, CancellationToken ct = default);

    /// <summary>Forgets the held password.</summary>
    void Lock(MailboxCredentialKey key);

    Task<List<MailboxFolderDto>> ListFoldersAsync(MailboxCredentialKey key, CancellationToken ct = default);

    Task<PaginatedResult<MailboxMessageSummaryDto>> ListMessagesAsync(
        MailboxCredentialKey key, string folder, int page, int pageSize, CancellationToken ct = default);

    /// <summary>The message, marked read. Remote images are fetched and inlined only when <paramref name="loadRemoteImages"/>.</summary>
    Task<MailboxMessageDto> GetMessageAsync(
        MailboxCredentialKey key, string folder, uint uid, bool loadRemoteImages, CancellationToken ct = default);

    Task<MailboxAttachmentContent> GetAttachmentAsync(
        MailboxCredentialKey key, string folder, uint uid, int index, CancellationToken ct = default);

    Task<MailboxDraftDto> GetDraftAsync(
        MailboxCredentialKey key, MailboxComposeMode mode, string folder, uint uid, string? language, CancellationToken ct = default);

    Task<MailboxSendResultDto> SendAsync(MailboxCredentialKey key, MailboxSendRequest request, CancellationToken ct = default);

    Task SetReadAsync(MailboxCredentialKey key, string folder, uint uid, bool isRead, CancellationToken ct = default);

    /// <summary>Moves the message to Trash, or erases it when it is already there.</summary>
    Task<MailboxDeleteResultDto> DeleteAsync(MailboxCredentialKey key, string folder, uint uid, CancellationToken ct = default);

    Task<MailboxFolderDto> CreateFolderAsync(MailboxCredentialKey key, string name, CancellationToken ct = default);
}
