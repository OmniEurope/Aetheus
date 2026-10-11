// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services.Email;

public enum EmailSendStatus
{
    /// <summary>The SMTP server accepted the message.</summary>
    Sent,

    /// <summary>No sender can be used, so nothing was attempted.</summary>
    NotConfigured,

    /// <summary>An SMTP connection was attempted and the message was not accepted.</summary>
    Failed
}
