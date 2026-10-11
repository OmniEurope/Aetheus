// SPDX-License-Identifier: EUPL-1.2
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// MailKit IMAP and SMTP clients. The server certificate is validated by the platform's default rules:
/// the self-signed certificate mail-setup starts with is refused, and the page says so until the mail
/// server's TLS certificate is installed.
/// </summary>
public sealed class MailKitMailboxConnector : IMailboxConnector
{
    private const int TimeoutMilliseconds = 20_000;

    public async Task<IMailboxSession> OpenAsync(MailboxEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var client = new ImapClient { Timeout = TimeoutMilliseconds };
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.ImapPort, SecureSocketOptions.SslOnConnect, ct).ConfigureAwait(false);
            await client.AuthenticateAsync(endpoint.Username, endpoint.Password, ct).ConfigureAwait(false);
            return new Session(client, endpoint.Host, endpoint.ImapPort);
        }
        catch (Exception exception)
        {
            // Mandatory: a client that failed half-way must not leak its socket, and the failure is
            // reported as the API's own exception with the server's reason.
            client.Dispose();
            if (MailboxErrors.Translate(exception, endpoint.Host, endpoint.ImapPort) is { } translated) throw translated;
            throw;
        }
    }

    public async Task SendAsync(MailboxEndpoint endpoint, MimeMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(message);
        using var client = new SmtpClient { Timeout = TimeoutMilliseconds };
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.SubmissionPort, SecureSocketOptions.StartTls, ct).ConfigureAwait(false);
            await client.AuthenticateAsync(endpoint.Username, endpoint.Password, ct).ConfigureAwait(false);
            // MailKit leaves the Bcc header out of what it transmits; the recipients still get the message.
            await client.SendAsync(message, ct).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Mandatory: the SMTP failure reaches the page with the server's reason.
            if (MailboxErrors.Translate(exception, endpoint.Host, endpoint.SubmissionPort) is { } translated) throw translated;
            throw;
        }
    }

    private sealed class Session(ImapClient client, string host, int port) : IMailboxSession
    {
        private const MessageSummaryItems ListItems = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope
            | MessageSummaryItems.Flags | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure
            | MessageSummaryItems.InternalDate;

        public Task<IReadOnlyList<MailboxFolderInfo>> ListFoldersAsync(CancellationToken ct) => RunAsync(async () =>
        {
            var folders = (await client.GetFoldersAsync(PersonalNamespace(), StatusItems.Count | StatusItems.Unread,
                subscribedOnly: false, ct).ConfigureAwait(false)).ToList();
            if (!folders.Any(folder => string.Equals(folder.FullName, client.Inbox.FullName, StringComparison.OrdinalIgnoreCase)))
            {
                await client.Inbox.StatusAsync(StatusItems.Count | StatusItems.Unread, ct).ConfigureAwait(false);
                folders.Insert(0, client.Inbox);
            }
            return (IReadOnlyList<MailboxFolderInfo>)folders
                .Where(folder => (folder.Attributes & (FolderAttributes.NoSelect | FolderAttributes.NonExistent)) == 0)
                .Select(folder => new MailboxFolderInfo(folder.FullName, folder.Name,
                    MailboxFolderRoles.Resolve(folder.FullName, folder.Name, folder.Attributes), folder.Count, folder.Unread))
                .ToList();
        });

        public Task<MailboxPage> ListMessagesAsync(string folder, int skip, int take, CancellationToken ct) => RunAsync(async () =>
        {
            var opened = await OpenFolderAsync(folder, FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            var total = opened.Count;
            if (skip >= total || take <= 0) return new MailboxPage(total, []);
            // Sequence numbers follow arrival order: the newest message is the last one.
            var max = total - 1 - skip;
            var min = Math.Max(0, max - take + 1);
            var summaries = await opened.FetchAsync(min, max, ListItems, ct).ConfigureAwait(false);
            return new MailboxPage(total, summaries.OrderByDescending(summary => summary.Index).Select(ToSummary).ToList());
        });

        public Task<long?> GetMessageSizeAsync(string folder, uint uid, CancellationToken ct) => RunAsync(async () =>
        {
            var opened = await OpenFolderAsync(folder, FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            var sizes = await opened.FetchAsync([new UniqueId(uid)], MessageSummaryItems.UniqueId | MessageSummaryItems.Size, ct)
                .ConfigureAwait(false);
            return sizes.Count == 0 ? null : (long?)(sizes[0].Size ?? 0);
        });

        public Task<MailboxFetchedMessage?> GetMessageAsync(string folder, uint uid, CancellationToken ct) => RunAsync(async () =>
        {
            var opened = await OpenFolderAsync(folder, FolderAccess.ReadOnly, ct).ConfigureAwait(false);
            var id = new UniqueId(uid);
            var flags = await opened.FetchAsync([id], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct).ConfigureAwait(false);
            if (flags.Count == 0) return null;
            var message = await opened.GetMessageAsync(id, ct).ConfigureAwait(false);
            return new MailboxFetchedMessage(uid, message, IsSeen(flags[0]));
        });

        public Task SetSeenAsync(string folder, uint uid, bool seen, CancellationToken ct) => RunAsync(async () =>
        {
            var opened = await OpenFolderAsync(folder, FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            var request = new StoreFlagsRequest(seen ? StoreAction.Add : StoreAction.Remove, MessageFlags.Seen) { Silent = true };
            await opened.StoreAsync(new UniqueId(uid), request, ct).ConfigureAwait(false);
            return true;
        });

        public Task MoveAsync(string folder, uint uid, string destination, CancellationToken ct) => RunAsync(async () =>
        {
            var target = await client.GetFolderAsync(destination, ct).ConfigureAwait(false);
            var opened = await OpenFolderAsync(folder, FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            await opened.MoveToAsync(new UniqueId(uid), target, ct).ConfigureAwait(false);
            return true;
        });

        public Task DeletePermanentlyAsync(string folder, uint uid, CancellationToken ct) => RunAsync(async () =>
        {
            var opened = await OpenFolderAsync(folder, FolderAccess.ReadWrite, ct).ConfigureAwait(false);
            var id = new UniqueId(uid);
            await opened.StoreAsync(id, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct)
                .ConfigureAwait(false);
            await opened.ExpungeAsync([id], ct).ConfigureAwait(false);
            return true;
        });

        public Task<string> CreateFolderAsync(string name, CancellationToken ct) => RunAsync(async () =>
        {
            var root = client.GetFolder(PersonalNamespace());
            var created = await root.CreateAsync(name, isMessageFolder: true, ct).ConfigureAwait(false);
            return (created ?? throw new InvalidOperationException($"The server did not create folder {name}.")).FullName;
        });

        public Task AppendAsync(string folder, MimeMessage message, bool seen, CancellationToken ct) => RunAsync(async () =>
        {
            var target = await client.GetFolderAsync(folder, ct).ConfigureAwait(false);
            await target.AppendAsync(new AppendRequest(message, seen ? MessageFlags.Seen : MessageFlags.None), ct)
                .ConfigureAwait(false);
            return true;
        });

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (client.IsConnected)
                    await client.DisconnectAsync(quit: true).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ImapProtocolException or ImapCommandException)
            {
                // Mandatory: the work is done or already failed; a failed LOGOUT changes neither.
            }
            finally
            {
                client.Dispose();
            }
        }

        private FolderNamespace PersonalNamespace() =>
            client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('.', string.Empty);

        private async Task<IMailFolder> OpenFolderAsync(string path, FolderAccess access, CancellationToken ct)
        {
            var folder = await client.GetFolderAsync(path, ct).ConfigureAwait(false);
            if (!folder.IsOpen || folder.Access < access)
                await folder.OpenAsync(access, ct).ConfigureAwait(false);
            return folder;
        }

        private async Task<T> RunAsync<T>(Func<Task<T>> action)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Mandatory: an IMAP failure reaches the page as the API's own exception with the server's reason.
                if (MailboxErrors.Translate(exception, host, port) is { } translated) throw translated;
                throw;
            }
        }

        private static MailboxSummary ToSummary(IMessageSummary summary) => new(
            summary.UniqueId.Id,
            MailboxAddresses.Format(summary.Envelope?.From),
            MailboxAddresses.Format(summary.Envelope?.To),
            summary.Envelope?.Subject ?? string.Empty,
            summary.Envelope?.Date ?? summary.InternalDate ?? DateTimeOffset.MinValue,
            IsSeen(summary),
            summary.Attachments?.Any() == true,
            (long)(summary.Size ?? 0));

        private static bool IsSeen(IMessageSummary summary) =>
            summary.Flags is { } flags && (flags & MessageFlags.Seen) != 0;
    }
}
