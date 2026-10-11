// SPDX-License-Identifier: EUPL-1.2
using MimeKit;

namespace Aetheus.Back.Components.Mail.Mailbox;

/// <summary>Address lists as the page shows them and as the compose fields take them back.</summary>
public static class MailboxAddresses
{
    /// <summary>"Name &lt;address&gt;" for a named mailbox, the bare address otherwise, comma separated.</summary>
    public static string Format(IEnumerable<InternetAddress>? addresses) =>
        addresses is null ? string.Empty : string.Join(", ", addresses.Select(Format));

    private static string Format(InternetAddress address) => address switch
    {
        MailboxAddress mailbox when !string.IsNullOrWhiteSpace(mailbox.Name) => $"{mailbox.Name} <{mailbox.Address}>",
        MailboxAddress mailbox => mailbox.Address,
        GroupAddress group => Format(group.Members),
        _ => address.ToString()
    };

    /// <summary>
    /// Parses a comma or semicolon separated list typed in a compose field. A token that is not an
    /// address is refused by name rather than dropped.
    /// </summary>
    public static List<MailboxAddress> Parse(string? value, string fieldName)
    {
        var result = new List<MailboxAddress>();
        if (string.IsNullOrWhiteSpace(value)) return result;
        foreach (var entry in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!MailboxAddress.TryParse(entry, out var address) || !address.Address.Contains('@', StringComparison.Ordinal))
                throw new BadRequestException($"'{entry}' in {fieldName} is not an e-mail address.");
            result.Add(address);
        }
        return result;
    }
}
