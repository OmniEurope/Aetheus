// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Settings;

/// <summary>Why no e-mail sender can be used, or <see cref="None"/> when one can.</summary>
public enum EmailSenderProblem
{
    None = 0,

    /// <summary>The default sender needs a mail domain on a server Aetheus manages, and there is none.</summary>
    NoMailDomain = 1,

    /// <summary>The domain has no active no-reply mailbox yet.</summary>
    NoReplyAccountMissing = 2,

    /// <summary>The no-reply mailbox exists, but Aetheus holds no password for it in its secret store.</summary>
    NoReplyPasswordUnknown = 3,

    /// <summary>The custom SMTP server lacks its host or its sender address.</summary>
    CustomIncomplete = 4,

    /// <summary>The custom SMTP server names a user name but no password is stored for it.</summary>
    CustomPasswordMissing = 5
}
