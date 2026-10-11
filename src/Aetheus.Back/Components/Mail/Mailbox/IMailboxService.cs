// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// Recette R1-17. The integrated mailbox of one account of a managed mail server. Every call names the
/// signed-in reader (<paramref name="key"/>.UserId): the mailbox password that reader typed is the only
/// one used. A call made while no password is held is refused with <see cref="ConflictException"/>;
/// a login the server refuses forgets the held password and is refused the same way.
/// </summary>
public interface IMailboxService
{
    Task<MailClientSettingsDto> GetClientSettingsAsync(int serverId, CancellationToken ct = default);

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
