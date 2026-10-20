// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Tests;

public class OperationTargetValidatorTests
{
    [Fact]
    public void AgentSelfUpdate_AlwaysValid()
    {
        Assert.True(OperationTargetValidator.IsValid(OperationKind.AgentSelfUpdate, null));
        Assert.True(OperationTargetValidator.IsValid(OperationKind.AgentSelfUpdate, ""));
        Assert.True(OperationTargetValidator.IsValid(OperationKind.AgentSelfUpdate, "anything"));
    }

    [Fact]
    public void None_AlwaysInvalid()
    {
        Assert.False(OperationTargetValidator.IsValid(OperationKind.None, "valid-target"));
        Assert.False(OperationTargetValidator.IsValid(OperationKind.None, null));
    }

    // Cross-agent deploy: the app name is also the systemd instance + on-disk dir name, so it must be
    // letters/digits/underscore/dash only (no dots, slashes, whitespace, or path-traversal).
    [Theory]
    [InlineData("toto")]
    [InlineData("my-app")]
    [InlineData("App_01")]
    [InlineData("a")]
    public void PipelineDeploy_ValidAppName_ReturnsTrue(string app)
        => Assert.True(OperationTargetValidator.IsValid(OperationKind.PipelineDeploy, app));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("../etc")]
    [InlineData("app/with/slash")]
    [InlineData("app.with.dot")]
    [InlineData("app with space")]
    [InlineData("app;rm -rf")]
    [InlineData("aetheus-app@toto")]
    public void PipelineDeploy_InvalidAppName_ReturnsFalse(string? app)
        => Assert.False(OperationTargetValidator.IsValid(OperationKind.PipelineDeploy, app));

    [Fact]
    public void PipelineDeploy_AppNameOver64Chars_ReturnsFalse()
        => Assert.False(OperationTargetValidator.IsValid(OperationKind.PipelineDeploy, new string('a', 65)));

    // S-FEAT-W8KN: install targets are the resolved apt package names in the closed allow-list.
    [Theory]
    [InlineData("nginx")]
    [InlineData("apache2")]
    [InlineData("docker.io")]
    [InlineData("postgresql")]
    [InlineData("mysql-server")]
    [InlineData("mongodb-org")]
    [InlineData("redis-server")]
    [InlineData("dovecot-core")]
    [InlineData("certbot")]
    public void ServiceInstall_AllowListedAptPackage_ReturnsTrue(string pkg)
        => Assert.True(OperationTargetValidator.IsValid(OperationKind.ServiceInstall, pkg));

    // Uninstall is restricted to the Removable subset - protected packages (databases, ufw/fail2ban,
    // docker) are installable but NOT uninstallable.
    [Theory]
    [InlineData("nginx")]
    [InlineData("apache2")]
    [InlineData("postfix")]
    [InlineData("dovecot-core")]
    [InlineData("certbot")]
    public void ServiceUninstall_RemovablePackage_ReturnsTrue(string pkg)
        => Assert.True(OperationTargetValidator.IsValid(OperationKind.ServiceUninstall, pkg));

    [Theory]
    [InlineData("docker.io")]
    [InlineData("postgresql")]
    [InlineData("mysql-server")]
    [InlineData("redis-server")]
    [InlineData("ufw")]
    [InlineData("fail2ban")]
    public void ServiceUninstall_ProtectedPackage_ReturnsFalse(string pkg)
    {
        Assert.True(OperationTargetValidator.IsValid(OperationKind.ServiceInstall, pkg));   // installable
        Assert.False(OperationTargetValidator.IsValid(OperationKind.ServiceUninstall, pkg)); // not removable
    }

    // Service keys are NOT valid install targets - the backend resolves them to apt names first.
    [Theory]
    [InlineData("docker")]      // service key; apt name is docker.io
    [InlineData("mysql")]       // service key; apt name is mysql-server
    [InlineData("evil-package")]
    [InlineData("nginx; rm -rf /")]
    [InlineData("")]
    [InlineData(null)]
    public void ServiceInstall_ServiceKeyOrJunk_ReturnsFalse(string? pkg)
    {
        Assert.False(OperationTargetValidator.IsValid(OperationKind.ServiceInstall, pkg));
        Assert.False(OperationTargetValidator.IsValid(OperationKind.ServiceUninstall, pkg));
    }

    // S-TECH-87: the target is a TeamSpeak ServerQuery command line - spaces/=/escapes are fine, but a
    // raw CR/LF would smuggle an extra ServerQuery line and must be rejected.
    [Theory]
    [InlineData("clientkick clid=42 reasonid=5 reasonmsg=Being\\srude")]
    [InlineData("channelcreate channel_name=Lobby channel_flag_permanent=1")]
    [InlineData("serveredit virtualserver_name=My\\sServer")]
    [InlineData("banlist")]
    public void TeamspeakServerQuery_ValidCommand_ReturnsTrue(string target)
    {
        Assert.True(OperationTargetValidator.IsValid(OperationKind.TeamspeakServerQuery, target));
    }

    [Theory]
    [InlineData("clientkick clid=1\nlogin serveradmin stolen")] // newline injection
    [InlineData("banadd uid=x\rmessage")]                        // carriage return
    [InlineData("")]                                              // empty
    [InlineData("   ")]                                           // whitespace only
    public void TeamspeakServerQuery_ControlCharsOrEmpty_ReturnsFalse(string target)
    {
        Assert.False(OperationTargetValidator.IsValid(OperationKind.TeamspeakServerQuery, target));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NonSelfUpdate_NullOrWhitespace_Invalid(string? target)
    {
        Assert.False(OperationTargetValidator.IsValid(OperationKind.DockerRestartContainer, target));
        Assert.False(OperationTargetValidator.IsValid(OperationKind.ServiceStart, target));
    }

    [Theory]
    [InlineData(OperationKind.DockerRestartContainer)]
    [InlineData(OperationKind.DockerStartContainer)]
    [InlineData(OperationKind.DockerStopContainer)]
    [InlineData(OperationKind.DockerPullImage)]
    public void DockerOps_ValidTarget_ReturnsTrue(OperationKind kind)
    {
        Assert.True(OperationTargetValidator.IsValid(kind, "nginx"));
        Assert.True(OperationTargetValidator.IsValid(kind, "my-app:latest"));
        Assert.True(OperationTargetValidator.IsValid(kind, "registry.io/org/app:v1.2.3"));
    }

    [Theory]
    [InlineData(OperationKind.DockerRestartContainer)]
    [InlineData(OperationKind.DockerPullImage)]
    public void DockerOps_InvalidTarget_ReturnsFalse(OperationKind kind)
    {
        Assert.False(OperationTargetValidator.IsValid(kind, ".invalid"));
        Assert.False(OperationTargetValidator.IsValid(kind, "-starts-with-dash"));
    }

    [Theory]
    [InlineData(OperationKind.ServiceStart)]
    [InlineData(OperationKind.ServiceStop)]
    [InlineData(OperationKind.ServiceRestart)]
    [InlineData(OperationKind.ServiceStatus)]
    public void ServiceOps_ValidTarget_ReturnsTrue(OperationKind kind)
    {
        Assert.True(OperationTargetValidator.IsValid(kind, "nginx"));
        Assert.True(OperationTargetValidator.IsValid(kind, "my_service.socket"));
    }

    [Theory]
    [InlineData(OperationKind.ServiceStart)]
    [InlineData(OperationKind.ServiceStop)]
    public void ServiceOps_InvalidTarget_ReturnsFalse(OperationKind kind)
    {
        Assert.False(OperationTargetValidator.IsValid(kind, ".bad"));
        Assert.False(OperationTargetValidator.IsValid(kind, "-nope"));
    }

    [Theory]
    [InlineData(OperationKind.ApacheReload)]
    [InlineData(OperationKind.ApacheTestConfig)]
    public void ApacheOps_ValidTargets(OperationKind kind)
    {
        Assert.True(OperationTargetValidator.IsValid(kind, "-"));
        Assert.True(OperationTargetValidator.IsValid(kind, "apache2"));
    }

    [Theory]
    [InlineData("example.com.conf")]
    [InlineData("000-default.conf")]
    public void ApacheGetConfig_ValidSiteFile_ReturnsTrue(string site)
        => Assert.True(OperationTargetValidator.IsValid(OperationKind.ApacheGetConfig, site));

    [Theory]
    [InlineData("../../etc/passwd")]      // traversal
    [InlineData("example.com")]           // no .conf
    [InlineData("site;rm -rf")]           // metacharacters
    public void ApacheGetConfig_InvalidSiteFile_ReturnsFalse(string site)
        => Assert.False(OperationTargetValidator.IsValid(OperationKind.ApacheGetConfig, site));

    [Theory]
    [InlineData(OperationKind.ApacheGetHtaccess)]
    [InlineData(OperationKind.ApacheSaveHtaccess)]
    public void ApacheHtaccess_ValidDocumentRoot_ReturnsTrue(OperationKind kind)
    {
        Assert.True(OperationTargetValidator.IsValid(kind, "/var/www/html"));
        Assert.True(OperationTargetValidator.IsValid(kind, "/srv/sites/example.com"));
    }

    [Theory]
    [InlineData(OperationKind.ApacheGetHtaccess)]
    [InlineData(OperationKind.ApacheSaveHtaccess)]
    public void ApacheHtaccess_InvalidDocumentRoot_ReturnsFalse(OperationKind kind)
    {
        Assert.False(OperationTargetValidator.IsValid(kind, "relative/path"));   // not absolute
        Assert.False(OperationTargetValidator.IsValid(kind, "/var/www/../../etc")); // traversal
        Assert.False(OperationTargetValidator.IsValid(kind, "/var/www; rm -rf /")); // metacharacters
        Assert.False(OperationTargetValidator.IsValid(kind, ""));
    }

    [Fact]
    public void DockerTargetRegex_MatchesLongPaths()
    {
        Assert.Matches(OperationTargetValidator.DockerTargetRegex(), "registry.io/org/app:v1.2.3");
    }

    [Fact]
    public void ServiceNameRegex_MatchesTypicalNames()
    {
        Assert.Matches(OperationTargetValidator.ServiceNameRegex(), "nginx");
        Assert.Matches(OperationTargetValidator.ServiceNameRegex(), "my_service.socket");
    }

    [Theory]
    [InlineData(OperationKind.ServiceEnable)]
    public void ServiceEnable_ValidatesLikeServiceName(OperationKind kind)
    {
        Assert.True(OperationTargetValidator.IsValid(kind, "apache2"));
        Assert.True(OperationTargetValidator.IsValid(kind, "nginx.service"));
        Assert.False(OperationTargetValidator.IsValid(kind, ".bad"));
        Assert.False(OperationTargetValidator.IsValid(kind, "-nope"));
    }

    // Phase 3: cron target is the job id (also the cron.d filename suffix) - strict, no dots/slashes.
    [Theory]
    [InlineData(OperationKind.CronSave)]
    [InlineData(OperationKind.CronDelete)]
    public void CronOps_ValidId_ReturnsTrue(OperationKind kind)
    {
        Assert.True(OperationTargetValidator.IsValid(kind, "job-7"));
        Assert.True(OperationTargetValidator.IsValid(kind, "abc_123"));
    }

    [Theory]
    [InlineData(OperationKind.CronSave)]
    [InlineData(OperationKind.CronDelete)]
    public void CronOps_InvalidId_ReturnsFalse(OperationKind kind)
    {
        Assert.False(OperationTargetValidator.IsValid(kind, "has.dot"));     // dot → cron ignores the file
        Assert.False(OperationTargetValidator.IsValid(kind, "../escape"));   // path traversal
        Assert.False(OperationTargetValidator.IsValid(kind, "bad id"));      // space
        Assert.False(OperationTargetValidator.IsValid(kind, ""));            // empty
    }

    // S-FEAT-W8KN: mail setup target is the mail domain (FQDN). Hostname/selector/email/quota travel
    // in env vars and the password via stdin - they are validated agent-side, not here.
    [Theory]
    [InlineData("example.com")]
    [InlineData("mail.example.co.uk")]
    public void MailSetup_ValidDomain_ReturnsTrue(string target)
        => Assert.True(OperationTargetValidator.IsValid(OperationKind.MailSetup, target));

    [Theory]
    [InlineData("not a domain")]
    [InlineData("localhost")]          // no TLD
    [InlineData("evil.com; rm -rf /")] // shell metachars
    [InlineData("")]
    [InlineData(null)]
    public void MailSetup_InvalidDomain_ReturnsFalse(string? target)
        => Assert.False(OperationTargetValidator.IsValid(OperationKind.MailSetup, target));

    // Mail removal / read ops (audit "dead shell action" migration). Same target shapes as the add ops:
    // remove-domain = domain, delete-account/remove-alias = email, dkim-read = DKIM selector.
    [Theory]
    [InlineData(OperationKind.MailRemoveDomain, "example.com", true)]
    [InlineData(OperationKind.MailRemoveDomain, "bad domain!", false)]
    [InlineData(OperationKind.MailDeleteAccount, "user@example.com", true)]
    [InlineData(OperationKind.MailDeleteAccount, "not-an-email", false)]
    [InlineData(OperationKind.MailRemoveAlias, "alias@example.com", true)]
    [InlineData(OperationKind.MailRemoveAlias, "no-at", false)]
    [InlineData(OperationKind.MailDkimRead, "example.com", true)]  // The operation target is the domain.
    [InlineData(OperationKind.MailDkimRead, "default", false)]
    [InlineData(OperationKind.MailDkimRead, "bad selector!", false)]
    public void MailRemoveReadOps_ValidateTarget(OperationKind kind, string target, bool expected)
        => Assert.Equal(expected, OperationTargetValidator.IsValid(kind, target));

    // TeamSpeak graceful-restart (target unused - the backend sends the "-" placeholder) + log read
    // (target is the install path). Note: the global not-empty guard means "target unused" ops still
    // require the non-empty "-" sentinel, not a truly empty string.
    [Fact]
    public void TeamspeakGracefulRestart_DashTarget_ReturnsTrue()
        => Assert.True(OperationTargetValidator.IsValid(OperationKind.TeamspeakGracefulRestart, "-"));

    [Theory]
    [InlineData("/opt/teamspeak", true)]
    [InlineData("/opt/ts3/../etc", false)]   // traversal
    [InlineData("relative/path", false)]     // not absolute
    [InlineData("", false)]
    public void TeamspeakGetLogs_ValidatesInstallPath(string target, bool expected)
        => Assert.Equal(expected, OperationTargetValidator.IsValid(OperationKind.TeamspeakGetLogs, target));

    // Portsentry setup (audit "dead shell action" migration): the scan mode is the task target
    // (alphanumeric, 1-16 chars). The TCP/UDP port lists travel in env vars, re-validated agent-side.
    [Theory]
    [InlineData("atcp", true)]
    [InlineData("tcp", true)]
    [InlineData("audp", true)]
    [InlineData("", false)]
    [InlineData("a tcp", false)]        // space
    [InlineData("atcp;rm", false)]      // shell metachars
    [InlineData("way-too-long-mode-name", false)] // > 16 chars + hyphen
    public void PortsentrySetup_ValidatesMode(string target, bool expected)
        => Assert.Equal(expected, OperationTargetValidator.IsValid(OperationKind.PortsentrySetup, target));

    [Fact]
    public void UnknownKind_ReturnsFalse()
    {
        Assert.False(OperationTargetValidator.IsValid((OperationKind)999, "target"));
    }
}
