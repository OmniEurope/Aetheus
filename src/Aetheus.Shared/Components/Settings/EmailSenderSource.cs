// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Settings;

/// <summary>Where the e-mails Aetheus sends leave from.</summary>
public enum EmailSenderSource
{
    /// <summary>No sender can be used: nothing is sent, and the problem says why.</summary>
    None = 0,

    /// <summary>The default: the no-reply account of the mail domain Aetheus manages.</summary>
    ManagedNoReply = 1,

    /// <summary>The SMTP server set in the platform settings.</summary>
    Custom = 2
}
