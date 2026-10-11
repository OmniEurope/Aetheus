// SPDX-License-Identifier: EUPL-1.2
using MimeKit;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>One logged-in IMAP connection; disposing it logs out.</summary>
public interface IMailboxSession : IAsyncDisposable
{
    Task<IReadOnlyList<MailboxFolderInfo>> ListFoldersAsync(CancellationToken ct);

    /// <summary>One page of the folder, newest first, read from the server's sequence numbers.</summary>
    Task<MailboxPage> ListMessagesAsync(string folder, int skip, int take, CancellationToken ct);

    /// <summary>The server-declared size in bytes (IMAP RFC822.SIZE), read without downloading the message; null when no message has that UID.</summary>
    Task<long?> GetMessageSizeAsync(string folder, uint uid, CancellationToken ct);

    /// <summary>The whole message, or null when no message has that UID any more.</summary>
    Task<MailboxFetchedMessage?> GetMessageAsync(string folder, uint uid, CancellationToken ct);

    Task SetSeenAsync(string folder, uint uid, bool seen, CancellationToken ct);

    Task MoveAsync(string folder, uint uid, string destination, CancellationToken ct);

    /// <summary>Flags the message deleted and expunges it: it is gone from the server.</summary>
    Task DeletePermanentlyAsync(string folder, uint uid, CancellationToken ct);

    /// <summary>Creates a folder at the top of the personal namespace and returns its path.</summary>
    Task<string> CreateFolderAsync(string name, CancellationToken ct);

    Task AppendAsync(string folder, MimeMessage message, bool seen, CancellationToken ct);
}
