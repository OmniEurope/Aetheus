// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>PLAN-005: every Mail operation kind maps to one exact mail-manage / mail-setup argv, and every
/// malformed field is refused before any process is spawned.</summary>
public class MailManageCommandBuilderTests
{
    private const string Manage = "/usr/local/lib/aetheus/mail-manage";

    private static readonly Dictionary<string, string> NoEnv = new(StringComparer.Ordinal);

    private static string[] Argv(OperationKind kind, string target, Dictionary<string, string>? env = null)
    {
        var command = MailManageCommandBuilder.Build(kind, target, env ?? NoEnv);
        Assert.NotNull(command);
        return [.. command.Argv];
    }

    [Theory]
    [InlineData(OperationKind.MailStartPostfix, "service postfix start")]
    [InlineData(OperationKind.MailStopPostfix, "service postfix stop")]
    [InlineData(OperationKind.MailRestartPostfix, "service postfix restart")]
    [InlineData(OperationKind.MailReloadPostfix, "service postfix reload")]
    [InlineData(OperationKind.MailStartDovecot, "service dovecot start")]
    [InlineData(OperationKind.MailStopDovecot, "service dovecot stop")]
    [InlineData(OperationKind.MailRestartDovecot, "service dovecot restart")]
    [InlineData(OperationKind.MailReloadDovecot, "service dovecot reload")]
    [InlineData(OperationKind.MailFlushQueue, "queue-flush")]
    [InlineData(OperationKind.MailViewQueue, "queue-list")]
    [InlineData(OperationKind.MailTestConfig, "check")]
    public void FixedServiceAndQueueKinds_RouteThroughTheManageHelper(OperationKind kind, string expected)
        => Assert.Equal(["-n", Manage, .. expected.Split(' ')], Argv(kind, "-"));

    [Fact]
    public void Logs_UseDefaultLinesAndOptionalLiteralFilter()
    {
        Assert.Equal(["-n", Manage, "logs", "rspamd", "100"], Argv(OperationKind.MailGetLogs, "rspamd"));
        var env = new Dictionary<string, string>
        {
            [MailSetupEnv.LogLines] = "250",
            [MailSetupEnv.LogFilter] = "4F2A1B3C9D"
        };
        Assert.Equal(["-n", Manage, "logs", "postfix", "250", "4F2A1B3C9D"], Argv(OperationKind.MailGetLogs, "postfix", env));
    }

    [Theory]
    [InlineData("sshd", null, null)]            // unit outside the managed list
    [InlineData("postfix", "0", null)]          // line count out of range
    [InlineData("postfix", "3000", null)]       // line count out of range
    [InlineData("postfix", "50", "$(id)")]      // command substitution in the filter
    [InlineData("postfix", "50", "a b")]        // whitespace in the filter
    public void Logs_RefuseInvalidUnitLinesOrFilter(string unit, string? lines, string? filter)
    {
        var env = new Dictionary<string, string>();
        if (lines is not null) env[MailSetupEnv.LogLines] = lines;
        if (filter is not null) env[MailSetupEnv.LogFilter] = filter;
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailGetLogs, unit, env));
    }

    [Fact]
    public void AddDomain_PassesTheSelectorOnlyWhenProvided()
    {
        Assert.Equal(["-n", Manage, "add-domain", "example.com"], Argv(OperationKind.MailAddDomain, "example.com"));
        Assert.Equal(["-n", Manage, "add-domain", "example.com", "s2026"],
            Argv(OperationKind.MailAddDomain, "example.com", new() { [MailSetupEnv.DkimSelector] = "s2026" }));
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailAddDomain, "example.com",
            new Dictionary<string, string> { [MailSetupEnv.DkimSelector] = "bad selector" }));
    }

    [Fact]
    public void DkimRead_TargetsTheDomainWithSelectorFromEnv()
    {
        Assert.Equal(["-n", Manage, "dkim-read", "example.com", "default"],
            Argv(OperationKind.MailDkimRead, "example.com", new() { [MailSetupEnv.DkimSelector] = "default" }));
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailDkimRead, "example.com", NoEnv));
    }

    [Fact]
    public void InstallCertificate_TargetsTheHostname()
    {
        Assert.Equal(["-n", Manage, "install-cert", "mail.example.com"], Argv(OperationKind.MailInstallCertificate, "mail.example.com"));
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailInstallCertificate, "../../etc", NoEnv));
        Assert.Equal(["-n", Manage, "install-cert", "mail.example.com", "admin@example.com"],
            Argv(OperationKind.MailInstallCertificate, "mail.example.com", new() { [MailSetupEnv.CertificateEmail] = "admin@example.com" }));
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailInstallCertificate, "mail.example.com",
            new Dictionary<string, string> { [MailSetupEnv.CertificateEmail] = "x;id" }));
    }

    [Fact]
    public void SpamConfigure_RequiresOrderedInvariantScores()
    {
        var env = new Dictionary<string, string>
        {
            [MailSetupEnv.SpamRejectScore] = "15",
            [MailSetupEnv.SpamAddHeaderScore] = "6.5",
            [MailSetupEnv.SpamGreylistScore] = "4"
        };
        Assert.Equal(["-n", Manage, "spam-configure", "15", "6.5", "4"], Argv(OperationKind.MailSpamConfigure, "-", env));

        env[MailSetupEnv.SpamGreylistScore] = "7";   // greylist above add_header
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailSpamConfigure, "-", env));

        env[MailSetupEnv.SpamGreylistScore] = "4;id"; // injection attempt
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailSpamConfigure, "-", env));
    }

    [Fact]
    public void SpamLearn_SendsTheMessageOverStdinOnly()
    {
        const string message = "Subject: buy now\n\nspam body";
        var command = MailManageCommandBuilder.Build(OperationKind.MailSpamLearn, "ham",
            new Dictionary<string, string> { [MailSetupEnv.LearnMessage] = message });
        Assert.NotNull(command);
        Assert.Equal(["-n", Manage, "spam-learn", "ham"], command.Argv);
        Assert.Equal(message, command.Stdin);
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailSpamLearn, "virus",
            new Dictionary<string, string> { [MailSetupEnv.LearnMessage] = message }));
    }

    [Theory]
    [InlineData("rspamd:restart", true)]
    [InlineData("opendkim:reload", true)]
    [InlineData("ssh:restart", false)]
    [InlineData("postfix:enable", false)]
    [InlineData("postfix", false)]
    public void ServiceControl_AcceptsOnlyManagedUnitsAndVerbs(string target, bool valid)
    {
        var command = MailManageCommandBuilder.Build(OperationKind.MailServiceControl, target, NoEnv);
        if (!valid)
        {
            Assert.Null(command);
            return;
        }
        Assert.NotNull(command);
        Assert.Equal(["-n", Manage, "service", .. target.Split(':')], command.Argv);
    }

    [Fact]
    public void SendTest_TakesSenderFromEnvAndRecipientAsTarget()
    {
        Assert.Equal(["-n", Manage, "send-test", "admin@example.com", "bob@example.org"],
            Argv(OperationKind.MailSendTest, "bob@example.org", new() { [MailSetupEnv.Sender] = "admin@example.com" }));
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailSendTest, "bob@example.org",
            new Dictionary<string, string> { [MailSetupEnv.Sender] = "x;id" }));
    }

    [Fact]
    public void QueueDeleteAndQuotaReport_ValidateTheirTargets()
    {
        Assert.Equal(["-n", Manage, "queue-delete", "4F2A1B3C9D"], Argv(OperationKind.MailQueueDelete, "4F2A1B3C9D"));
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailQueueDelete, "../etc", NoEnv));
        Assert.Equal(["-n", Manage, "quota-report"], Argv(OperationKind.MailQuotaReport, "-"));
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailQuotaReport, "all", NoEnv));
    }

    [Fact]
    public void Purge_RunsTheManageHelperPurgeOnTheDashTargetOnly()
    {
        // "Réinstaller la messagerie" (R2-083 / R2-088): one fixed argv, nothing from the request reaches it.
        Assert.Equal(["-n", Manage, "purge"], Argv(OperationKind.MailPurge, "-"));
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailPurge, "example.com", NoEnv));
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailPurge, "-;id", NoEnv));
    }

    [Fact]
    public void Purge_IsAMailManagementKindTheMailExecutorClaims()
    {
        var executor = new MailOperationExecutor(
            Microsoft.Extensions.Options.Options.Create(new Aetheus.Agent.Core.Configuration.AetheusAgentOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MailOperationExecutor>.Instance);
        Assert.Equal(AgentCapabilities.MailManagement, AgentCapabilities.RequiredFor(OperationKind.MailPurge));
        Assert.True(executor.CanHandle(OperationKind.MailPurge));
        Assert.True(OperationTargetValidator.IsValid(OperationKind.MailPurge, "-"));
        Assert.False(OperationTargetValidator.IsValid(OperationKind.MailPurge, "all"));
    }

    [Fact]
    public void Setup_PassesTheSpamFilterChoice()
    {
        var env = new Dictionary<string, string>
        {
            [MailSetupEnv.Hostname] = "mail.example.com",
            [MailSetupEnv.DkimSelector] = "default",
            [MailSetupEnv.AdminEmail] = "admin@example.com",
            [MailSetupEnv.QuotaMb] = "1024",
            [MailSetupEnv.AdminPassword] = "SecureP@ss123!",
            [MailSetupEnv.SpamFilter] = "rspamd"
        };
        Assert.Equal("rspamd", Argv(OperationKind.MailSetup, "example.com", env)[^1]);
        env[MailSetupEnv.SpamFilter] = "spamassassin";
        Assert.Null(MailManageCommandBuilder.Build(OperationKind.MailSetup, "example.com", env));
    }

    [Fact]
    public void EveryMailCapabilityKind_IsHandledByTheMailExecutor()
    {
        // A Mail* kind that maps to mail.manage but that the executor does not claim would be dispatched
        // to no executor at all; pin the executor to the whole capability range.
        var executor = new MailOperationExecutor(
            Microsoft.Extensions.Options.Options.Create(new Aetheus.Agent.Core.Configuration.AetheusAgentOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MailOperationExecutor>.Instance);
        var mailKinds = Enum.GetValues<OperationKind>()
            .Where(kind => AgentCapabilities.RequiredFor(kind) == AgentCapabilities.MailManagement)
            .ToList();
        Assert.Contains(OperationKind.MailQuotaReport, mailKinds);
        Assert.All(mailKinds, kind => Assert.True(executor.CanHandle(kind), $"{kind} is not handled"));
    }
}
