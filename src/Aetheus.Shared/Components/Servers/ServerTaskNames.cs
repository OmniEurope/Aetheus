// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Docker;
using Aetheus.Shared.Components.Mail;

namespace Aetheus.Shared.Components.Servers;

/// <summary>Recette R-12: the starts of the names the backend gives to the server tasks a page launches. The pages find
/// such a task again by name when the user comes back while it still runs, so the backend and the pages read the
/// same constants and a renamed task cannot silently hide an action in progress.</summary>
public static class ServerTaskNames
{
    /// <summary>The firewall tasks: "Firewall allow 22/tcp", "Firewall enable".</summary>
    public const string FirewallPrefix = "Firewall ";

    /// <summary>The read-only system update check (dry run).</summary>
    public const string PatchCheck = "System update check (dry-run)";

    /// <summary>The system package upgrade.</summary>
    public const string PatchUpgrade = "System package upgrade";

    /// <summary>The reboot of a server ordered by its schedule (a blocking task: see the front's ServerBlockingTasks).</summary>
    public const string ScheduledReboot = "Scheduled reboot";

    /// <summary>The scan of the host's listening ports.</summary>
    public const string PortObservation = "Observe listening ports";

    /// <summary>The service actions of the Services page: "Service Start - nginx".</summary>
    public const string ServiceActionPrefix = "Service ";

    /// <summary>The verb of a package installation; its task is "Install - nginx".</summary>
    public const string InstallVerb = "Install";

    /// <summary>The verb of a package removal; its task is "Uninstall - nginx".</summary>
    public const string UninstallVerb = "Uninstall";

    /// <summary>The start of the installation task names.</summary>
    public const string InstallPrefix = InstallVerb + " - ";

    /// <summary>The start of the removal task names.</summary>
    public const string UninstallPrefix = UninstallVerb + " - ";

    // ---- Mail: the actions of the mail page the follower finds again on arrival -------------------------------------
    // The setup ("Mail - full setup"), the leftovers check and the purge are followed by their own components and are
    // not in the mail page's list.

    /// <summary>The start of a mail domain addition; its task is "Mail - add domain example.org".</summary>
    public const string MailAddDomainPrefix = "Mail - add domain ";

    /// <summary>The start of a mail domain removal.</summary>
    public const string MailRemoveDomainPrefix = "Mail - remove domain ";

    /// <summary>The start of a mail account creation.</summary>
    public const string MailAddAccountPrefix = "Mail - add account ";

    /// <summary>The start of a mail account deletion.</summary>
    public const string MailDeleteAccountPrefix = "Mail - delete account ";

    /// <summary>The start of a mail alias creation; its task is "Mail - alias a@x → b@x".</summary>
    public const string MailAddAliasPrefix = "Mail - alias ";

    /// <summary>The start of a mail alias removal.</summary>
    public const string MailRemoveAliasPrefix = "Mail - remove alias ";

    /// <summary>The start of a DKIM key rotation; its task is "Mail - rotate DKIM key for example.org (old → new)".</summary>
    public const string MailRotateDkimPrefix = "Mail - rotate DKIM key for ";

    /// <summary>The preparation of the administrator access to the mailboxes.</summary>
    public const string MailPrepareAdministratorAccess = "Mail - prepare the administrator access";

    /// <summary>The installation of the spam filter.</summary>
    public const string MailSpamInstall = "Mail - install spam filter (rspamd)";

    /// <summary>The change of the spam filter thresholds.</summary>
    public const string MailSpamThresholds = "Mail - spam thresholds";

    /// <summary>The start of the training of the spam filter with a message; its task is "Mail - learn spam" or
    /// "Mail - learn ham".</summary>
    public const string MailLearnPrefix = "Mail - learn ";

    /// <summary>The name of the task that trains the spam filter with one message.</summary>
    public static string MailLearn(bool isSpam) => MailLearnPrefix + (isSpam ? "spam" : "ham");

    /// <summary>The start of the certificate task; its task is "Mail - TLS certificate (mail.example.org)". The
    /// obtain and the install buttons of the status list queue the same task.</summary>
    public const string MailTlsCertificatePrefix = "Mail - TLS certificate (";

    /// <summary>The name of the certificate task of a mail host.</summary>
    public static string MailTlsCertificate(string hostname) => $"{MailTlsCertificatePrefix}{hostname})";

    /// <summary>The start of the delivery test; its task is "Mail - delivery test to bob@example.org".</summary>
    public const string MailDeliveryTestPrefix = "Mail - delivery test to ";

    /// <summary>The measure of the mailbox usage.</summary>
    public const string MailMailboxUsage = "Mail - mailbox usage";

    /// <summary>The purge of the mail stack (reinstall); the reinstall component finds this task again by its exact name.</summary>
    public const string MailPurge = "Mail - erase the mail stack (reinstall)";

    /// <summary>The start of the name of a mail service action; its task is "Mail - RestartSpamFilter".</summary>
    public const string MailActionPrefix = "Mail - ";

    /// <summary>The name of the task of a mail service action (start, stop, restart of a mail service).</summary>
    public static string MailActionTask(MailAction action) => MailActionPrefix + action;

    /// <summary>The starts of the mail task names the mail page's follower adopts on arrival: the actions it launches
    /// itself. The setup, the leftovers check and the purge are absent on purpose (their own components follow them);
    /// so are the service actions, the server check, the certificate, the delivery test and the mailbox usage (the
    /// status list follows those by id, and adopts them by the names above) and the reads (queue, logs, DKIM key).</summary>
    public static readonly IReadOnlyList<string> MailAdoptedPrefixes =
    [
        MailAddDomainPrefix, MailRemoveDomainPrefix, MailAddAccountPrefix, MailDeleteAccountPrefix,
        MailAddAliasPrefix, MailRemoveAliasPrefix, MailRotateDkimPrefix,
        MailPrepareAdministratorAccess, MailSpamInstall, MailSpamThresholds, MailLearnPrefix,
        MailActionTask(MailAction.StartSpamFilter), MailActionTask(MailAction.StopSpamFilter), MailActionTask(MailAction.RestartSpamFilter)
    ];

    // ---- Certbot ------------------------------------------------------------------------------------------------------

    /// <summary>The start of every Certbot task name ("Certbot RenewalCheck - a.example.org"); the Certbot page finds such
    /// a task again by it when the user comes back while it still runs.</summary>
    public const string CertbotPrefix = "Certbot";

    // ---- Docker -------------------------------------------------------------------------------------------------------

    /// <summary>The start of a stack action; its task is "Compose Up - monitoring".</summary>
    public const string ComposeActionPrefix = "Compose ";

    /// <summary>The start of the task that runs a command in a container from the shell panel; its task is
    /// "Docker exec - abc123def456" (the first twelve characters of the container id).</summary>
    public const string DockerExecPrefix = "Docker exec - ";

    /// <summary>The start of the task that reads a container's log.</summary>
    public const string DockerLogsPrefix = "Docker logs - ";

    /// <summary>The start of the task that inspects a container.</summary>
    public const string DockerInspectPrefix = "Docker inspect - ";

    /// <summary>The start of the task that reads a container's environment.</summary>
    public const string DockerEnvPrefix = "Docker env - ";

    /// <summary>The start of the task that lists a directory of a container.</summary>
    public const string DockerListPrefix = "Docker ls - ";

    /// <summary>The reads of the Docker page, which last a few seconds and change nothing: a container's log, its
    /// inspection, its environment, a directory listing, a stack's compose file. The page never adopts them on arrival
    /// (it would veil for a read), though they share the "Docker" prefix of its actions.</summary>
    public static readonly IReadOnlyList<string> DockerReadPrefixes =
    [
        DockerLogsPrefix, DockerInspectPrefix, DockerEnvPrefix, DockerListPrefix, DockerTaskNames.ComposeFilePrefix
    ];
}
