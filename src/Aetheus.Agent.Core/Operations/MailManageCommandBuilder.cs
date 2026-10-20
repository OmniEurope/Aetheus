// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Full <c>sudo</c> argv for one mail helper call, plus the optional stdin payload (secrets, messages).</summary>
internal sealed record MailHelperCommand(IReadOnlyList<string> Argv, string? Stdin = null);

/// <summary>Build exact argv for root-owned mail helpers and reject invalid fields before process
/// launch; helpers validate again at the privilege boundary. Send secrets and messages over
/// stdin.</summary>
internal static class MailManageCommandBuilder
{
    // Root-owned helpers deposited by install-agent-linux.sh (--enable-mail-setup); the matching
    // /etc/sudoers.d/aetheus-mail grants exactly these two binaries, NOPASSWD.
    internal const string SetupHelperPath = "/usr/local/lib/aetheus/mail-setup";
    internal const string ManageHelperPath = "/usr/local/lib/aetheus/mail-manage";

    private const int DefaultLogLines = 100;

    // One entry per Mail kind: the helper argv builder, fed the validated target and the task env.
    private static readonly Dictionary<OperationKind, Func<string, IReadOnlyDictionary<string, string>, MailHelperCommand?>> s_builders = new()
    {
        [OperationKind.MailSetup] = Setup,
        [OperationKind.MailStartPostfix] = (_, _) => Manage("service", "postfix", "start"),
        [OperationKind.MailStopPostfix] = (_, _) => Manage("service", "postfix", "stop"),
        [OperationKind.MailRestartPostfix] = (_, _) => Manage("service", "postfix", "restart"),
        [OperationKind.MailReloadPostfix] = (_, _) => Manage("service", "postfix", "reload"),
        [OperationKind.MailStartDovecot] = (_, _) => Manage("service", "dovecot", "start"),
        [OperationKind.MailStopDovecot] = (_, _) => Manage("service", "dovecot", "stop"),
        [OperationKind.MailRestartDovecot] = (_, _) => Manage("service", "dovecot", "restart"),
        [OperationKind.MailReloadDovecot] = (_, _) => Manage("service", "dovecot", "reload"),
        [OperationKind.MailFlushQueue] = (_, _) => Manage("queue-flush"),
        [OperationKind.MailViewQueue] = (_, _) => Manage("queue-list"),
        [OperationKind.MailTestConfig] = (_, _) => Manage("check"),
        [OperationKind.MailGetLogs] = Logs,
        [OperationKind.MailAddDomain] = AddDomain,
        [OperationKind.MailAddAccount] = AddAccount,
        [OperationKind.MailAddAlias] = AddAlias,
        [OperationKind.MailDkimRotate] = RotateDkim,
        [OperationKind.MailChangePassword] = ChangePassword,
        [OperationKind.MailRemoveDomain] = (target, _) => MailValidation.IsValidDomainName(target) ? Manage("remove-domain", target) : null,
        [OperationKind.MailDeleteAccount] = DeleteAccount,
        [OperationKind.MailRemoveAlias] = (target, _) => MailValidation.IsValidEmail(target) ? Manage("remove-alias", target) : null,
        [OperationKind.MailDkimRead] = DkimRead,
        [OperationKind.MailInstallCertificate] = InstallCertificate,
        [OperationKind.MailSpamInstall] = (target, _) => target == "-" ? Manage("spam-install") : null,
        [OperationKind.MailSpamConfigure] = SpamConfigure,
        [OperationKind.MailSpamLearn] = SpamLearn,
        [OperationKind.MailServiceControl] = (target, _) => ServiceControl(target),
        [OperationKind.MailSendTest] = SendTest,
        [OperationKind.MailQueueDelete] = (target, _) => MailValidation.IsValidQueueId(target) ? Manage("queue-delete", target) : null,
        [OperationKind.MailQuotaReport] = (target, _) => target == "-" ? Manage("quota-report") : null,
        [OperationKind.MailRetryTuning] = (target, _) => target == "-" ? Manage("retry-tuning") : null,
        [OperationKind.MailPurge] = (target, _) => target == "-" ? Manage("purge") : null,
        [OperationKind.MailMasterAccess] = MasterAccess,
        [OperationKind.MailSetQuota] = SetQuota
    };

    public static MailHelperCommand? Build(OperationKind kind, string target, IReadOnlyDictionary<string, string> env) =>
        s_builders.TryGetValue(kind, out var build) ? build(target, env) : null;

    private static MailHelperCommand Manage(params string[] args) =>
        new(["-n", ManageHelperPath, .. args]);

    private static MailHelperCommand? Setup(string domain, IReadOnlyDictionary<string, string> env)
    {
        env.TryGetValue(MailSetupEnv.Hostname, out var hostname);
        env.TryGetValue(MailSetupEnv.DkimSelector, out var selector);
        env.TryGetValue(MailSetupEnv.AdminEmail, out var email);
        env.TryGetValue(MailSetupEnv.QuotaMb, out var quotaRaw);
        env.TryGetValue(MailSetupEnv.AdminPassword, out var password);
        var spam = env.TryGetValue(MailSetupEnv.SpamFilter, out var spamRaw) ? spamRaw : "none";
        if (!MailValidation.IsValidDomainName(domain)
            || !MailValidation.IsValidDomainName(hostname)
            || !MailValidation.IsValidDkimSelector(selector)
            || !MailValidation.IsValidEmail(email)
            || !int.TryParse(quotaRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var quota)
            || !MailValidation.IsValidQuotaMb(quota)
            || !MailValidation.IsValidPassword(password)
            || spam is not ("rspamd" or "none"))
            return null;
        // Recette R1-41: the optional second stdin line is the Dovecot master user's password; mail-setup
        // hands it to mail-manage master-set once the stack runs. Present but invalid refuses the setup.
        var stdin = password + "\n";
        if (env.TryGetValue(MailSetupEnv.MasterPassword, out var master) && !string.IsNullOrEmpty(master))
        {
            if (!MailValidation.IsValidPassword(master)) return null;
            stdin += master + "\n";
        }
        return new MailHelperCommand(
            ["-n", SetupHelperPath, hostname!, domain, selector!, email!, quota.ToString(CultureInfo.InvariantCulture), spam],
            stdin);
    }

    private static MailHelperCommand? MasterAccess(string target, IReadOnlyDictionary<string, string> env)
    {
        env.TryGetValue(MailSetupEnv.MasterPassword, out var password);
        return target == "-" && MailValidation.IsValidPassword(password)
            ? new MailHelperCommand(["-n", ManageHelperPath, "master-set"], password + "\n")
            : null;
    }

    private static MailHelperCommand? Logs(string unit, IReadOnlyDictionary<string, string> env)
    {
        if (!MailValidation.IsValidManagedUnit(unit)) return null;
        var lines = DefaultLogLines;
        if (env.TryGetValue(MailSetupEnv.LogLines, out var raw)
            && (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out lines) || !MailValidation.IsValidLogLines(lines)))
            return null;
        var lineText = lines.ToString(CultureInfo.InvariantCulture);
        if (!env.TryGetValue(MailSetupEnv.LogFilter, out var filter) || string.IsNullOrEmpty(filter))
            return Manage("logs", unit, lineText);
        return MailValidation.IsValidLogFilter(filter) ? Manage("logs", unit, lineText, filter) : null;
    }

    private static MailHelperCommand? AddDomain(string domain, IReadOnlyDictionary<string, string> env)
    {
        if (!MailValidation.IsValidDomainName(domain)) return null;
        if (!env.TryGetValue(MailSetupEnv.DkimSelector, out var selector) || string.IsNullOrEmpty(selector))
            return Manage("add-domain", domain);
        return MailValidation.IsValidDkimSelector(selector) ? Manage("add-domain", domain, selector) : null;
    }

    private static MailHelperCommand? AddAccount(string email, IReadOnlyDictionary<string, string> env)
    {
        env.TryGetValue(MailSetupEnv.Domain, out var domain);
        env.TryGetValue(MailSetupEnv.QuotaMb, out var quotaRaw);
        env.TryGetValue(MailSetupEnv.AccountPassword, out var password);
        if (!MailValidation.IsValidEmail(email)
            || !MailValidation.IsValidDomainName(domain)
            || !int.TryParse(quotaRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var quota)
            || !MailValidation.IsValidQuotaMb(quota)
            || !MailValidation.IsValidPassword(password))
            return null;
        return new MailHelperCommand(
            ["-n", ManageHelperPath, "add-account", email, domain!, quota.ToString(CultureInfo.InvariantCulture)],
            password + "\n");
    }

    private static MailHelperCommand? SetQuota(string email, IReadOnlyDictionary<string, string> env)
    {
        env.TryGetValue(MailSetupEnv.QuotaMb, out var quotaRaw);
        return MailValidation.IsValidEmail(email)
            && int.TryParse(quotaRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var quota)
            && MailValidation.IsValidQuotaMb(quota)
            ? Manage("set-quota", email, quota.ToString(CultureInfo.InvariantCulture))
            : null;
    }

    private static MailHelperCommand? AddAlias(string source, IReadOnlyDictionary<string, string> env)
    {
        env.TryGetValue(MailSetupEnv.AliasDestination, out var destination);
        return MailValidation.IsValidEmail(source) && MailValidation.IsValidEmail(destination)
            ? Manage("add-alias", source, destination!)
            : null;
    }

    private static MailHelperCommand? RotateDkim(string domain, IReadOnlyDictionary<string, string> env)
    {
        env.TryGetValue(MailSetupEnv.NewSelector, out var selector);
        return MailValidation.IsValidDomainName(domain) && MailValidation.IsValidDkimSelector(selector)
            ? Manage("dkim-rotate", domain, selector!)
            : null;
    }

    private static MailHelperCommand? ChangePassword(string email, IReadOnlyDictionary<string, string> env)
    {
        env.TryGetValue(MailSetupEnv.AccountPassword, out var password);
        return MailValidation.IsValidEmail(email) && MailValidation.IsValidPassword(password)
            ? new MailHelperCommand(["-n", ManageHelperPath, "change-password", email], password + "\n")
            : null;
    }

    private static MailHelperCommand? DeleteAccount(string email, IReadOnlyDictionary<string, string> env)
    {
        env.TryGetValue(MailSetupEnv.Domain, out var domain);
        return MailValidation.IsValidEmail(email) && MailValidation.IsValidDomainName(domain)
            ? Manage("delete-account", email, domain!)
            : null;
    }

    private static MailHelperCommand? DkimRead(string domain, IReadOnlyDictionary<string, string> env)
    {
        env.TryGetValue(MailSetupEnv.DkimSelector, out var selector);
        return MailValidation.IsValidDomainName(domain) && MailValidation.IsValidDkimSelector(selector)
            ? Manage("dkim-read", domain, selector!)
            : null;
    }

    private static MailHelperCommand? InstallCertificate(string hostname, IReadOnlyDictionary<string, string> env)
    {
        if (!MailValidation.IsValidDomainName(hostname)) return null;
        if (!env.TryGetValue(MailSetupEnv.CertificateEmail, out var email) || string.IsNullOrEmpty(email))
            return Manage("install-cert", hostname);
        return MailValidation.IsValidEmail(email) ? Manage("install-cert", hostname, email) : null;
    }

    private static MailHelperCommand? SpamConfigure(string target, IReadOnlyDictionary<string, string> env)
    {
        env.TryGetValue(MailSetupEnv.SpamRejectScore, out var reject);
        env.TryGetValue(MailSetupEnv.SpamAddHeaderScore, out var header);
        env.TryGetValue(MailSetupEnv.SpamGreylistScore, out var grey);
        if (target != "-"
            || !MailValidation.IsValidSpamScoreText(reject)
            || !MailValidation.IsValidSpamScoreText(header)
            || !MailValidation.IsValidSpamScoreText(grey)
            || !MailValidation.AreSpamThresholdsOrdered(
                double.Parse(grey!, CultureInfo.InvariantCulture),
                double.Parse(header!, CultureInfo.InvariantCulture),
                double.Parse(reject!, CultureInfo.InvariantCulture)))
            return null;
        return Manage("spam-configure", reject!, header!, grey!);
    }

    private static MailHelperCommand? SpamLearn(string kind, IReadOnlyDictionary<string, string> env)
    {
        env.TryGetValue(MailSetupEnv.LearnMessage, out var message);
        return kind is "spam" or "ham" && !string.IsNullOrEmpty(message) && message.Length <= MailValidation.MaxLearnMessageLength
            ? new MailHelperCommand(["-n", ManageHelperPath, "spam-learn", kind], message)
            : null;
    }

    private static MailHelperCommand? ServiceControl(string target)
    {
        if (!MailValidation.IsValidServiceTarget(target)) return null;
        var parts = target.Split(':');
        return Manage("service", parts[0], parts[1]);
    }

    private static MailHelperCommand? SendTest(string recipient, IReadOnlyDictionary<string, string> env)
    {
        env.TryGetValue(MailSetupEnv.Sender, out var sender);
        return MailValidation.IsValidEmail(recipient) && MailValidation.IsValidEmail(sender)
            ? Manage("send-test", sender!, recipient)
            : null;
    }
}
