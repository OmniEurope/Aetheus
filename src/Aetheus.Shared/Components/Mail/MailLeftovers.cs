// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Mail;

/// <summary>
/// Recette R3-23 / R3-26: what the mail leftovers check looks at, the reference command of 2026-10-12
/// (<c>dpkg -l</c> on the mail packages, <c>ls -d</c> of the configuration directories and <c>ss -ltn</c> on the
/// mail ports) written once for the agent that reads them and the page that names them.
/// </summary>
public static class MailLeftovers
{
    /// <summary>The dpkg name patterns of the mail stack packages (postfix, dovecot, OpenDKIM and its library, rspamd).</summary>
    public static IReadOnlyList<string> PackagePatterns { get; } = ["postfix*", "dovecot*", "opendkim*", "libopendkim*", "rspamd*"];

    /// <summary>The directories and files the mail stack writes.</summary>
    public static IReadOnlyList<string> Paths { get; } =
    [
        "/etc/postfix", "/etc/dovecot", "/etc/opendkim", "/etc/opendkim.conf", "/etc/rspamd",
        "/var/mail/vhosts", "/var/spool/postfix", "/var/lib/postfix"
    ];

    /// <summary>The ownership marker <c>mail-setup</c> writes and <c>mail-manage purge</c> removes last.</summary>
    public const string Marker = "/etc/aetheus/mail-stack.managed";

    /// <summary>The mail ports: SMTP, IMAP, submissions, submission, IMAPS, OpenDKIM milter, rspamd milter and controller.</summary>
    public static IReadOnlyList<int> Ports { get; } = [25, 143, 465, 587, 993, 8891, 11332, 11334];

    /// <summary>The kinds of a <c>LEFTOVER</c> line.</summary>
    public const string KindPackage = "package";
    public const string KindPath = "path";
    public const string KindMarker = "marker";
    public const string KindPort = "port";
}

/// <summary>One thing a mail stack left on the host: its kind (<see cref="MailLeftovers.KindPackage"/>…), its
/// name (package, path, port) and a detail (the dpkg state of a package, <c>rc</c> for configuration files).</summary>
public sealed record MailLeftoverDto
{
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

/// <summary>The parsed output of a <see cref="Aetheus.Shared.Components.Tasks.OperationKind.MailLeftoversCheck"/> task.
/// <see cref="IsComplete"/> is false when the closing <c>CLEAN</c> line is missing (the check did not finish):
/// a host is then never called clean.</summary>
public sealed record MailLeftoversReportDto
{
    public bool IsComplete { get; init; }
    public List<MailLeftoverDto> Items { get; init; } = [];
    public bool IsClean => IsComplete && Items.Count == 0;
}
