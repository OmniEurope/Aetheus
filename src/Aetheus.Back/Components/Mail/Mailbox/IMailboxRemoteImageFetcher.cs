// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>Fetches the remote images of a message the reader asked to see.</summary>
public interface IMailboxRemoteImageFetcher
{
    /// <summary>The images fetched, as <c>data:</c> URLs keyed by the URL the message wrote; a failed one is absent.</summary>
    Task<IReadOnlyDictionary<string, string>> FetchAsync(IReadOnlyList<string> urls, CancellationToken ct);
}
