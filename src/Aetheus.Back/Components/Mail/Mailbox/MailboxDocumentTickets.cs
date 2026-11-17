// SPDX-License-Identifier: EUPL-1.2
using System.Buffers.Text;
using System.Security.Cryptography;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>One sanitized message document waiting for its reading frame, and the mailbox it was read from.</summary>
public sealed record MailboxDocumentTicket(MailboxCredentialKey Key, string Document, DateTime ExpiresAt);

/// <summary>
/// The short-lived, single-use tickets that let the reading frame load a message document. A frame's
/// <c>src</c> cannot carry the Authorization header, and the long-lived JWT never goes into a URL: the
/// authorized read of the message issues a random ticket bound to the reader's held mailbox key, and the
/// frame's request redeems it once.
/// </summary>
public interface IMailboxDocumentTickets
{
    /// <summary>Holds <paramref name="document"/> for <paramref name="key"/> and returns its ticket.</summary>
    string Issue(MailboxCredentialKey key, string document);

    /// <summary>The live ticket, without consuming it; null when unknown, used or expired.</summary>
    MailboxDocumentTicket? Peek(string ticket);

    /// <summary>Consumes the ticket: the first call returns it, every later call returns null.</summary>
    MailboxDocumentTicket? Redeem(string ticket);
}

/// <summary>The in-memory <see cref="IMailboxDocumentTickets"/>, beside the in-memory password store; a singleton.</summary>
public sealed class MailboxDocumentTickets(TimeProvider timeProvider) : IMailboxDocumentTickets
{
    /// <summary>The frame loads right after the message arrives; a ticket left unused is worthless after this.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>Upper bound of waiting documents (a document can weigh a few megabytes); the oldest goes first.</summary>
    public const int MaxEntries = 100;

    private readonly Dictionary<string, MailboxDocumentTicket> _tickets = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string Issue(MailboxCredentialKey key, string document)
    {
        ArgumentException.ThrowIfNullOrEmpty(key.UserId);
        ArgumentNullException.ThrowIfNull(document);
        var ticket = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = Now();
        lock (_gate)
        {
            foreach (var expired in _tickets.Where(entry => entry.Value.ExpiresAt <= now).Select(entry => entry.Key).ToList())
                _tickets.Remove(expired);
            while (_tickets.Count >= MaxEntries)
                _tickets.Remove(_tickets.MinBy(entry => entry.Value.ExpiresAt).Key);
            _tickets[ticket] = new MailboxDocumentTicket(key, document, now + Lifetime);
        }
        return ticket;
    }

    public MailboxDocumentTicket? Peek(string ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        lock (_gate)
            return _tickets.TryGetValue(ticket, out var entry) && entry.ExpiresAt > Now() ? entry : null;
    }

    public MailboxDocumentTicket? Redeem(string ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        lock (_gate)
            return _tickets.Remove(ticket, out var entry) && entry.ExpiresAt > Now() ? entry : null;
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
}
