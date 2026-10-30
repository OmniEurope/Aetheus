// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Shared.Components.Rkhunter;

/// <summary>R-119: the environment key of <see cref="Aetheus.Shared.Components.Tasks.OperationKind.RkhunterSetup"/> and
/// the rule its value follows, shared so the backend and the agent agree on both. Absent when no warning mail is
/// wanted.</summary>
public static partial class RkhunterSetupEnv
{
    /// <summary>The address RKHunter mails its warnings to (MAIL-ON-WARNING in <c>/etc/rkhunter.conf</c>).</summary>
    public const string MailOnWarning = "AETHEUS_RKHUNTER_MAIL_ON_WARNING";

    /// <summary>An address the root-owned helper writes into <c>/etc/rkhunter.conf</c>: no quote, no space, no line
    /// break (<c>\z</c>, not <c>$</c>, which lets a final line feed through). The helper applies the same rule.</summary>
    public static bool IsValidMailOnWarning(string? address) =>
        !string.IsNullOrWhiteSpace(address) && MailOnWarningRegex().IsMatch(address);

    [GeneratedRegex(@"^[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}\z")]
    private static partial Regex MailOnWarningRegex();
}
