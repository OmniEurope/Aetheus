// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Servers.Events;

/// <summary>Reports the SSH changes one heartbeat revealed against the stored snapshot (root login,
/// password login, a key added to an authorized_keys file, a file the agent can no longer read).
/// Never raised for the first snapshot, which is the baseline.</summary>
public sealed record SshConfigChangedEvent(
    int ServerId,
    string ServerName,
    string Severity,
    string Message,
    DateTime DetectedAt,
    IReadOnlyList<SshChangeNotice>? Changes = null) : IDomainEvent;

/// <summary>One alerted SSH change in language-neutral form: a code and its arguments, worded by the mail in the
/// recipient's language (the English <c>Message</c> stays for the live toast and the logs).</summary>
public sealed record SshChangeNotice(string Code, IReadOnlyList<string> Args);
