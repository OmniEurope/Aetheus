// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Mail;

/// <summary>
/// Whether the DKIM public key of a mail domain is in the DNS records returned to the UI. Every other record is
/// computed by the backend; the key is read from the server, so it can be missing while the read runs or while
/// the agent that would read it is offline.
/// </summary>
public enum MailDkimKeyStatus
{
    /// <summary>The key is known: the DKIM record is complete.</summary>
    Available,

    /// <summary>The key is not known yet and a read task was queued on the server agent.</summary>
    Reading,

    /// <summary>The key is not known yet and the agent is offline, so no read could be queued.</summary>
    AgentOffline
}
