// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Shared.Components.Shared;

/// <summary>
/// S-FEAT-W8KN: shared mail-field validation used on BOTH sides of the typed
/// <see cref="Aetheus.Shared.Components.Tasks.OperationKind.MailSetup"/> operation - the backend
/// (<c>MailService</c>) rejects bad input before queuing, and the agent's <c>MailOperationExecutor</c>
/// re-validates as a last-line defence before invoking the root-owned <c>mail-setup</c> sudo helper.
/// Every charset deliberately excludes shell metacharacters so a validated field can never carry an
/// injection payload to the helper. <c>MailCommandHelper</c> (backend) delegates to these so there is
/// a single source of truth for the patterns.
/// </summary>
public static partial class MailValidation
{
    // Hostname / domain: letters, digits, hyphens, dots - fully-qualified (at least one dot, a TLD ≥ 2).
    [GeneratedRegex(@"^[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?)*\.[a-zA-Z]{2,}$")]
    private static partial Regex DomainRegex();

    // Basic email pattern.
    [GeneratedRegex(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$")]
    private static partial Regex EmailRegex();

    // DKIM selector: alphanumeric, hyphens, underscores.
    [GeneratedRegex(@"^[a-zA-Z0-9_-]+$")]
    private static partial Regex DkimSelectorRegex();

    public static bool IsValidDomainName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && DomainRegex().IsMatch(name);

    public static bool IsValidEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) && EmailRegex().IsMatch(email);

    public static bool IsValidDkimSelector(string? selector) =>
        !string.IsNullOrWhiteSpace(selector) && DkimSelectorRegex().IsMatch(selector);

    public static bool IsValidPassword(string? password)
    {
        if (string.IsNullOrEmpty(password)
            || password.Length is < PasswordPolicy.MinimumLength or > PasswordPolicy.MaximumLength)
            return false;
        // Reject control chars / NUL - they would break the helper's stdin read and could smuggle
        // a newline into the dovecot users file.
        return !password.Any(char.IsControl);
    }

    // Mailbox quota in MB - a plain non-negative integer bounded well above any realistic mailbox.
    public static bool IsValidQuotaMb(int quotaMb) => quotaMb is >= 0 and <= 1_048_576;

    // Mail-management field validation.

    /// <summary>Units whose journal and lifecycle the mail helper may touch.</summary>
    public static IReadOnlyList<string> ManagedUnits { get; } = ["postfix", "dovecot", "opendkim", "rspamd"];

    /// <summary>systemctl verbs the helper accepts for <see cref="ManagedUnits"/>.</summary>
    public static IReadOnlyList<string> ServiceActions { get; } = ["start", "stop", "restart", "reload"];

    // Postfix short (hex) and long (base-52) queue ids.
    [GeneratedRegex(@"^[0-9A-Za-z]{6,32}$")]
    private static partial Regex QueueIdRegex();

    // Literal journal filter: queue id, message-id token or address. No shell or regex metacharacters.
    [GeneratedRegex(@"^[A-Za-z0-9._@<>=-]{1,64}$")]
    private static partial Regex LogFilterRegex();

    // Spam score as the helper accepts it: up to three integer digits and two decimals.
    [GeneratedRegex(@"^[0-9]{1,3}(\.[0-9]{1,2})?$")]
    private static partial Regex SpamScoreRegex();

    public static bool IsValidManagedUnit(string? unit) => unit is not null && ManagedUnits.Contains(unit);

    /// <summary>Validates a <c>unit:action</c> service-control target, for example <c>rspamd:restart</c>.</summary>
    public static bool IsValidServiceTarget(string? target)
    {
        if (string.IsNullOrEmpty(target)) return false;
        var parts = target.Split(':');
        return parts.Length == 2 && IsValidManagedUnit(parts[0]) && ServiceActions.Contains(parts[1]);
    }

    public static bool IsValidQueueId(string? id) => !string.IsNullOrEmpty(id) && QueueIdRegex().IsMatch(id);

    public static bool IsValidLogFilter(string? filter) =>
        !string.IsNullOrEmpty(filter) && LogFilterRegex().IsMatch(filter);

    public static bool IsValidLogLines(int lines) => lines is >= 1 and <= 2000;

    /// <summary>Invariant-culture spam score string the helper can parse (for example <c>6.5</c>).</summary>
    public static bool IsValidSpamScoreText(string? score) =>
        !string.IsNullOrEmpty(score) && SpamScoreRegex().IsMatch(score);

    /// <summary>rspamd applies the highest matching action, so the three thresholds must be ordered.</summary>
    public static bool AreSpamThresholdsOrdered(double greylist, double addHeader, double reject) =>
        greylist is >= 0 and < 1000 && addHeader is >= 0 and < 1000 && reject is >= 0 and < 1000
        && greylist < addHeader && addHeader < reject;

    /// <summary>Upper bound of a message handed to <c>rspamc learn_*</c> (the helper truncates at 1 MiB).</summary>
    public const int MaxLearnMessageLength = 1_048_576;
}
