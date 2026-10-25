// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>
/// Recette R1-41 / R3-50. The Dovecot master user Aetheus opens a mailbox with, without that mailbox's
/// password: <c>mail-manage master-set</c> writes it as <see cref="MasterUser"/> with the master passdb and
/// <c>auth_master_user_separator = *</c>, and a mailbox is opened as <c>{address}*aetheus-master</c>. The
/// password is generated here, travels to the helper over stdin only, and is kept encrypted in the
/// platform secret store under <see cref="SecretKey"/>, one per server.
/// </summary>
public static class MailMasterAccess
{
    /// <summary>The master user's name, fixed in the mail-manage helper.</summary>
    public const string MasterUser = "aetheus-master";

    /// <summary>Dovecot's <c>auth_master_user_separator</c> as written by the helper.</summary>
    public const char Separator = '*';

    /// <summary>The first mail-manage helper version that has the <c>master-set</c> sub-command.</summary>
    public const int MinimumHelperVersion = 4;


    /// <summary>Platform secret holding the master password of one server.</summary>
    public static string SecretKey(int serverId) =>
        "MailMaster:server:" + serverId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The IMAP and SMTP login that opens <paramref name="email"/> as the master user.</summary>
    public static string Login(string email) => email + Separator + MasterUser;

    /// <summary>A new random master password (no character the helper or Dovecot treats specially).</summary>
    public static string NewPassword() => MailPasswordGenerator.NewPassword();
}
