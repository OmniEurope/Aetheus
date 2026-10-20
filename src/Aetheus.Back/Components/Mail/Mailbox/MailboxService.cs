// SPDX-License-Identifier: EUPL-1.2
using MimeKit;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>Open one IMAP connection per call with the reader credentials from <see
/// cref="IMailboxCredentialStore"/>, perform the operation and log out.</summary>
public sealed class MailboxService(
    IMailboxRepository repository,
    IMailboxConnector connector,
    IMailboxCredentialStore credentials,
    IMailboxRemoteImageFetcher imageFetcher,
    TimeProvider timeProvider) : IMailboxService
{
    /// <summary>The largest message (IMAP size) the mailbox downloads and decodes in memory.</summary>
    internal const long MaxMessageBytes = 25L * 1024 * 1024;

    private const int MaxFolderPathLength = 500;
    private static readonly char[] s_forbiddenFolderCharacters = ['/', '\\', '.', '*', '%', '"'];

    public async Task<MailClientSettingsDto> GetClientSettingsAsync(int serverId, CancellationToken ct = default) => new()
    {
        Host = await repository.GetMailHostAsync(serverId, ct).ConfigureAwait(false),
        ImapPort = MailboxPorts.Imaps,
        SubmissionPort = MailboxPorts.Submission
    };

    public async Task<PaginatedResult<MailboxDirectoryEntryDto>> ListDirectoryAsync(
        IReadOnlyCollection<int>? serverIds, PaginationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var size = Math.Clamp(request.PageSize, 1, 200);
        var number = Math.Max(1, request.Page);
        if (serverIds is { Count: 0 })
            return new PaginatedResult<MailboxDirectoryEntryDto> { Items = [], TotalCount = 0, Page = number, PageSize = size };
        var (items, total) = await repository.ListDirectoryAsync(
            serverIds, request.Search, number, size, request.SortBy, request.SortDescending, ct).ConfigureAwait(false);
        return new PaginatedResult<MailboxDirectoryEntryDto> { Items = items, TotalCount = total, Page = number, PageSize = size };
    }

    public async Task<MailboxAccessDto> GetAccessAsync(MailboxCredentialKey key, CancellationToken ct = default)
    {
        var account = await FindAccountAsync(key, ct).ConfigureAwait(false);
        return ToAccess(account, credentials.ExpiresAt(key), credentials.IsAdministratorAccess(key));
    }

    public async Task<MailboxAccessDto> UnlockAsync(MailboxCredentialKey key, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(password)) throw new BadRequestException("The mailbox password is required.");
        var account = await FindAccountAsync(key, ct).ConfigureAwait(false);
        // The login itself is the check: a refused one throws before anything is held.
        await using (await connector.OpenAsync(Endpoint(account, new MailboxHeldCredential(password, null)), ct).ConfigureAwait(false))
        {
        }
        var expiresAt = credentials.Store(key, password);
        return ToAccess(account, expiresAt, administratorAccess: false);
    }

    public void Lock(MailboxCredentialKey key) => credentials.Forget(key);

    public Task<List<MailboxFolderDto>> ListFoldersAsync(MailboxCredentialKey key, CancellationToken ct = default) =>
        WithSessionAsync(key, async (session, _) =>
            (await session.ListFoldersAsync(ct).ConfigureAwait(false)).Select(ToFolderDto).ToList(), ct);

    public Task<PaginatedResult<MailboxMessageSummaryDto>> ListMessagesAsync(
        MailboxCredentialKey key, string folder, int page, int pageSize, CancellationToken ct = default)
    {
        RequireFolder(folder);
        var size = Math.Clamp(pageSize, 1, MailboxLimits.MaxPageSize);
        var number = Math.Max(1, page);
        return WithSessionAsync(key, async (session, _) =>
        {
            var result = await session.ListMessagesAsync(folder, (number - 1) * size, size, ct).ConfigureAwait(false);
            return new PaginatedResult<MailboxMessageSummaryDto>
            {
                Items = result.Items.Select(MailboxMessageMapper.ToSummaryDto).ToList(),
                TotalCount = result.Total,
                Page = number,
                PageSize = size
            };
        }, ct);
    }

    public Task<MailboxMessageDto> GetMessageAsync(
        MailboxCredentialKey key, string folder, uint uid, bool loadRemoteImages, CancellationToken ct = default)
    {
        RequireFolder(folder);
        return WithSessionAsync(key, async (session, _) =>
        {
            var fetched = await FetchAsync(session, folder, uid, ct).ConfigureAwait(false);
            if (!fetched.Seen)
            {
                await session.SetSeenAsync(folder, uid, seen: true, ct).ConfigureAwait(false);
                fetched = fetched with { Seen = true };
            }
            IReadOnlyDictionary<string, string>? remoteImages = null;
            if (loadRemoteImages)
            {
                var urls = MailboxMessageMapper.RemoteImageUrls(fetched.Message);
                if (urls.Count > 0) remoteImages = await imageFetcher.FetchAsync(urls, ct).ConfigureAwait(false);
            }
            return MailboxMessageMapper.ToDto(folder, fetched, remoteImages);
        }, ct);
    }

    public Task<MailboxAttachmentContent> GetAttachmentAsync(
        MailboxCredentialKey key, string folder, uint uid, int index, CancellationToken ct = default)
    {
        RequireFolder(folder);
        return WithSessionAsync(key, async (session, _) =>
        {
            var fetched = await FetchAsync(session, folder, uid, ct).ConfigureAwait(false);
            var entity = fetched.Message.Attachments.ElementAtOrDefault(index)
                ?? throw new NotFoundException("This attachment is not in the message.");
            using var stream = new MemoryStream();
            if (entity is MimePart { Content: not null } part)
                await part.Content.DecodeToAsync(stream, ct).ConfigureAwait(false);
            else if (entity is MessagePart { Message: not null } embedded)
                await embedded.Message.WriteToAsync(stream, ct).ConfigureAwait(false);
            return new MailboxAttachmentContent(
                MailboxComposer.FileNameOf(entity, index), entity.ContentType.MimeType, stream.ToArray());
        }, ct);
    }

    public Task<MailboxDraftDto> GetDraftAsync(
        MailboxCredentialKey key, MailboxComposeMode mode, string folder, uint uid, string? language, CancellationToken ct = default)
    {
        if (mode == MailboxComposeMode.New) return Task.FromResult(new MailboxDraftDto());
        RequireFolder(folder);
        return WithSessionAsync(key, async (session, account) =>
        {
            var fetched = await FetchAsync(session, folder, uid, ct).ConfigureAwait(false);
            return MailboxComposer.BuildDraft(mode, fetched.Message, account.Email, language);
        }, ct);
    }

    public Task<MailboxSendResultDto> SendAsync(MailboxCredentialKey key, MailboxSendRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var replying = request.Mode != MailboxComposeMode.New;
        if (replying && (string.IsNullOrWhiteSpace(request.SourceFolder) || request.SourceUid is null))
            throw new BadRequestException("A reply or a forward names the message it answers.");
        MailboxOutgoingAttachments.Validate(request.Attachments);

        return WithSessionAsync(key, async (session, account, credential) =>
        {
            MimeMessage? original = null;
            if (replying)
                original = (await FetchAsync(session, request.SourceFolder!, request.SourceUid!.Value, ct).ConfigureAwait(false)).Message;
            var message = MailboxComposer.BuildMessage(
                request, new MailboxAddress(string.Empty, account.Email), original, timeProvider.GetUtcNow());
            await connector.SendAsync(Endpoint(account, credential), message, ct).ConfigureAwait(false);
            return await SaveToSentAsync(session, message, ct).ConfigureAwait(false);
        }, ct);
    }

    public Task SetReadAsync(MailboxCredentialKey key, string folder, uint uid, bool isRead, CancellationToken ct = default)
    {
        RequireFolder(folder);
        return WithSessionAsync(key, async (session, _) =>
        {
            await session.SetSeenAsync(folder, uid, isRead, ct).ConfigureAwait(false);
            return true;
        }, ct);
    }

    public Task<MailboxDeleteResultDto> DeleteAsync(MailboxCredentialKey key, string folder, uint uid, CancellationToken ct = default)
    {
        RequireFolder(folder);
        return WithSessionAsync(key, async (session, _) =>
        {
            var folders = await session.ListFoldersAsync(ct).ConfigureAwait(false);
            var current = folders.FirstOrDefault(candidate => candidate.Path == folder)
                ?? throw new NotFoundException("This folder does not exist in the mailbox.");
            if (current.Role == MailboxFolderRole.Trash)
            {
                await session.DeletePermanentlyAsync(folder, uid, ct).ConfigureAwait(false);
                return new MailboxDeleteResultDto { Permanent = true };
            }
            var trash = await FolderOfRoleAsync(session, folders, MailboxFolderRole.Trash, ct).ConfigureAwait(false);
            await session.MoveAsync(folder, uid, trash, ct).ConfigureAwait(false);
            return new MailboxDeleteResultDto { Permanent = false };
        }, ct);
    }

    public Task<MailboxFolderDto> CreateFolderAsync(MailboxCredentialKey key, string name, CancellationToken ct = default)
    {
        var trimmed = ValidFolderName(name);
        return WithSessionAsync(key, async (session, _) =>
        {
            var folders = await session.ListFoldersAsync(ct).ConfigureAwait(false);
            if (folders.Any(folder => string.Equals(folder.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
                throw new BadRequestException($"A folder named '{trimmed}' already exists.");
            var path = await session.CreateFolderAsync(trimmed, ct).ConfigureAwait(false);
            return new MailboxFolderDto { Path = path, Name = trimmed, Role = MailboxFolderRole.Other };
        }, ct);
    }

    /// <summary>The name of a new folder, refused when it would create a hierarchy or hold a wildcard.</summary>
    public static string ValidFolderName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > MailboxLimits.MaxFolderNameLength)
            throw new BadRequestException($"A folder name has 1 to {MailboxLimits.MaxFolderNameLength} characters.");
        if (trimmed.IndexOfAny(s_forbiddenFolderCharacters) >= 0 || trimmed.Any(char.IsControl))
            throw new BadRequestException("A folder name cannot hold / \\ . * % \" or a control character.");
        return trimmed;
    }

    private Task<T> WithSessionAsync<T>(
        MailboxCredentialKey key, Func<IMailboxSession, MailboxAccount, Task<T>> work, CancellationToken ct) =>
        WithSessionAsync(key, (session, account, _) => work(session, account), ct);

    private async Task<T> WithSessionAsync<T>(
        MailboxCredentialKey key, Func<IMailboxSession, MailboxAccount, MailboxHeldCredential, Task<T>> work, CancellationToken ct)
    {
        var account = await FindAccountAsync(key, ct).ConfigureAwait(false);
        var credential = credentials.TryUseCredential(key)
            ?? throw new ConflictException("The mailbox is locked: type its password to open it.");
        try
        {
            await using var session = await connector.OpenAsync(Endpoint(account, credential), ct).ConfigureAwait(false);
            return await work(session, account, credential).ConfigureAwait(false);
        }
        catch (ConflictException)
        {
            // Mandatory: the connector reports a refused login (IMAP or SMTP) as a conflict, and only that.
            // The held password no longer opens the mailbox (changed elsewhere): forget it, the page asks again.
            credentials.Forget(key);
            throw;
        }
    }

    private async Task<MailboxAccount> FindAccountAsync(MailboxCredentialKey key, CancellationToken ct)
    {
        var account = await repository.FindAccountAsync(key.ServerId, key.AccountId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("This mailbox does not exist on the server, or is disabled.");
        if (string.IsNullOrWhiteSpace(account.Host))
            throw new BadRequestException("The server has no host name or address to reach its mail service.");
        return account;
    }

    /// <summary>Downloads the message, refusing before the download one the server declares larger than <see cref="MaxMessageBytes"/>.</summary>
    private static async Task<MailboxFetchedMessage> FetchAsync(IMailboxSession session, string folder, uint uid, CancellationToken ct)
    {
        var size = await session.GetMessageSizeAsync(folder, uid, ct).ConfigureAwait(false);
        if (size > MaxMessageBytes)
            throw new BadRequestException(
                $"This message is {size / (1024 * 1024)} MB, over the {MaxMessageBytes / (1024 * 1024)} MB the mailbox opens.");
        return await session.GetMessageAsync(folder, uid, ct).ConfigureAwait(false)
            ?? throw new NotFoundException("This message is no longer in the folder.");
    }

    private static async Task<MailboxSendResultDto> SaveToSentAsync(IMailboxSession session, MimeMessage message, CancellationToken ct)
    {
        try
        {
            var folders = await session.ListFoldersAsync(ct).ConfigureAwait(false);
            var sent = await FolderOfRoleAsync(session, folders, MailboxFolderRole.Sent, ct).ConfigureAwait(false);
            await session.AppendAsync(sent, message, seen: true, ct).ConfigureAwait(false);
            return new MailboxSendResultDto { SavedToSent = true };
        }
        catch (Exception exception) when (exception is BadRequestException or NotFoundException)
        {
            // Mandatory: the message has already left; a copy the server refused does not make the send fail.
            return new MailboxSendResultDto { SavedToSent = false, SentCopyProblem = exception.Message };
        }
    }

    private static async Task<string> FolderOfRoleAsync(
        IMailboxSession session, IReadOnlyList<MailboxFolderInfo> folders, MailboxFolderRole role, CancellationToken ct) =>
        folders.FirstOrDefault(folder => folder.Role == role)?.Path
        ?? await session.CreateFolderAsync(MailboxFolderRoles.DefaultName(role), ct).ConfigureAwait(false);

    private static void RequireFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > MaxFolderPathLength)
            throw new BadRequestException("The folder is missing or too long.");
    }

    /// <summary>The typed password logs in as the address; the administrator access (R1-41) as the master login.</summary>
    internal static MailboxEndpoint Endpoint(MailboxAccount account, MailboxHeldCredential credential) =>
        new(account.Host, MailboxPorts.Imaps, MailboxPorts.Submission, credential.Login ?? account.Email, credential.Password);

    internal static MailboxAccessDto ToAccess(MailboxAccount account, DateTime? expiresAt, bool administratorAccess) => new()
    {
        AccountId = account.AccountId,
        Email = account.Email,
        Host = account.Host,
        ImapPort = MailboxPorts.Imaps,
        SubmissionPort = MailboxPorts.Submission,
        Username = account.Email,
        IsUnlocked = expiresAt is not null,
        UnlockedUntil = expiresAt,
        OpenedAsAdministrator = expiresAt is not null && administratorAccess
    };

    private static MailboxFolderDto ToFolderDto(MailboxFolderInfo folder) => new()
    {
        Path = folder.Path,
        Name = folder.Name,
        Role = folder.Role,
        Total = folder.Total,
        Unread = folder.Unread
    };
}
