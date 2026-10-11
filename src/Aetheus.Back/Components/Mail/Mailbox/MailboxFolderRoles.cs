// SPDX-License-Identifier: EUPL-1.2
using MailKit;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// The role of a folder: its IMAP SPECIAL-USE attribute when the server sends one (Dovecot does for
/// the mailboxes its configuration names), otherwise the usual English names, so a folder created by
/// another client is still recognised.
/// </summary>
public static class MailboxFolderRoles
{
    private static readonly (FolderAttributes Attribute, MailboxFolderRole Role)[] s_attributes =
    [
        (FolderAttributes.Inbox, MailboxFolderRole.Inbox),
        (FolderAttributes.Sent, MailboxFolderRole.Sent),
        (FolderAttributes.Drafts, MailboxFolderRole.Drafts),
        (FolderAttributes.Trash, MailboxFolderRole.Trash),
        (FolderAttributes.Junk, MailboxFolderRole.Junk),
        (FolderAttributes.Archive, MailboxFolderRole.Archive)
    ];

    private static readonly Dictionary<string, MailboxFolderRole> s_names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INBOX"] = MailboxFolderRole.Inbox,
        ["Sent"] = MailboxFolderRole.Sent,
        ["Sent Messages"] = MailboxFolderRole.Sent,
        ["Sent Items"] = MailboxFolderRole.Sent,
        ["Drafts"] = MailboxFolderRole.Drafts,
        ["Trash"] = MailboxFolderRole.Trash,
        ["Deleted Messages"] = MailboxFolderRole.Trash,
        ["Junk"] = MailboxFolderRole.Junk,
        ["Spam"] = MailboxFolderRole.Junk,
        ["Archive"] = MailboxFolderRole.Archive
    };

    /// <summary>The name a missing Sent or Trash folder is created with (Dovecot's stock special-use names).</summary>
    public static string DefaultName(MailboxFolderRole role) => role switch
    {
        MailboxFolderRole.Sent => "Sent",
        MailboxFolderRole.Trash => "Trash",
        MailboxFolderRole.Drafts => "Drafts",
        MailboxFolderRole.Junk => "Junk",
        MailboxFolderRole.Archive => "Archive",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "This role has no default folder name.")
    };

    public static MailboxFolderRole Resolve(string fullName, string name, FolderAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        ArgumentNullException.ThrowIfNull(name);
        if (string.Equals(fullName, "INBOX", StringComparison.OrdinalIgnoreCase))
            return MailboxFolderRole.Inbox;
        foreach (var (attribute, role) in s_attributes)
        {
            if ((attributes & attribute) != 0) return role;
        }
        // Only a top-level folder takes a role from its name: "Projects/Sent" is the user's own folder.
        return fullName == name && s_names.TryGetValue(name, out var named) ? named : MailboxFolderRole.Other;
    }
}
