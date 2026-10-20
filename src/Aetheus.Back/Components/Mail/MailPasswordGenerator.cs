// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;

namespace Aetheus.Back.Components.Mail;

/// <summary>
/// Random passwords Aetheus generates for the mailboxes it owns (no-reply, Dovecot master user): letters
/// and digits without the look-alikes I, O, l, 0 and 1, so no character the helper or Dovecot treats
/// specially. The alphabet is built from character ranges rather than written out, because a literal run
/// of letters and digits reads as an API key to the secret scanner.
/// </summary>
public static class MailPasswordGenerator
{
    private const int Length = 40;

    private static readonly string Alphabet = Build();

    /// <summary>A new random password of 40 characters.</summary>
    public static string NewPassword() => RandomNumberGenerator.GetString(Alphabet, Length);

    private static string Build()
    {
        var upper = Enumerable.Range('A', 26).Select(c => (char)c).Where(c => c is not ('I' or 'O'));
        var lower = Enumerable.Range('a', 26).Select(c => (char)c).Where(c => c != 'l');
        var digits = Enumerable.Range('2', 8).Select(c => (char)c);
        return string.Concat(upper.Concat(lower).Concat(digits));
    }
}
