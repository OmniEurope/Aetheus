// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Verify that agent mail-helper paths match installer-rendered root-owned templates and
/// exact sudoers grants. Resolve installer variables to compare the complete path
/// contract.</summary>
public sealed class MailHelperPathAuditTests
{
    [Fact]
    public void SetupHelperPath_matches_install_script()
        => Assert.Equal(MailOperationExecutor.SetupHelperPath, ResolveHelperPath("MAIL_SETUP_HELPER_PATH"));

    [Fact]
    public void ManageHelperPath_matches_install_script()
        => Assert.Equal(MailOperationExecutor.ManageHelperPath, ResolveHelperPath("MAIL_MANAGE_HELPER_PATH"));

    // Each helper is a template under deploy/agent-host-config the installer renders to its path.
    [Theory]
    [InlineData("MAIL_SETUP_HELPER_PATH", "mail/mail-setup")]
    [InlineData("MAIL_MANAGE_HELPER_PATH", "mail/mail-manage")]
    public void Each_helper_is_rendered_from_its_template(string varName, string template)
    {
        var script = ReadInstallScript();
        Assert.Matches(
            new Regex($@"render_host_config {Regex.Escape(template)} ""\${Regex.Escape(varName)}""", RegexOptions.None),
            script);
        Assert.StartsWith("#!/bin/sh\n", LinuxHostConfigTemplates.Read(template), StringComparison.Ordinal);
    }

    [Fact]
    public void Sudoers_grants_both_mail_helpers()
    {
        Assert.Contains(
            "render_host_config mail/sudoers.d/aetheus-mail \"$MAIL_MANAGE_SUDOERS_FILE\"",
            ReadInstallScript(),
            StringComparison.Ordinal);
        var script = LinuxHostConfigTemplates.Read("mail/sudoers.d/aetheus-mail");
        Assert.Contains("Cmnd_Alias AETHEUS_MAIL = #{MAIL_SETUP_HELPER_PATH}#", script, StringComparison.Ordinal);
        Assert.Contains("Cmnd_Alias AETHEUS_MAIL_MANAGE = #{MAIL_MANAGE_HELPER_PATH}#", script, StringComparison.Ordinal);
        // The NOPASSWD grant must reference both aliases, else mail-manage is written but unusable.
        Assert.Matches(new Regex(@"NOPASSWD:\s*AETHEUS_MAIL\b.*\bAETHEUS_MAIL_MANAGE\b"), script);
    }

    [Fact]
    public void Setup_gives_postfix_a_main_cf_before_the_first_postconf()
    {
        // Task 23162 (2026-10-03): a Postfix installed as "No configuration" has no main.cf, and the
        // first postconf -e died on it. The helper preseeds debconf and falls back to the packaged default.
        var script = LinuxHostConfigTemplates.Read("mail/mail-setup");
        var firstPostconf = script.IndexOf("postconf -e", StringComparison.Ordinal);
        Assert.True(script.IndexOf("postfix postfix/main_mailer_type select Internet Site", StringComparison.Ordinal) is > 0 and var preseed
            && preseed < script.IndexOf("apt-get install", StringComparison.Ordinal));
        Assert.True(script.IndexOf("/usr/share/postfix/main.cf.debian /etc/postfix/main.cf", StringComparison.Ordinal) is > 0 and var seed
            && seed < firstPostconf);
        // IPv4 only (2026-10-03): one reverse DNS record, the IPv4 one, is all the host needs.
        Assert.Contains("postconf -e \"inet_protocols = ipv4\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_unmasks_the_stack_before_enabling_it()
    {
        // Tasks 23257 and 23258 (2026-10-03): a postfix unit masked on the host refused enable.
        var script = LinuxHostConfigTemplates.Read("mail/mail-setup");
        var unmask = script.IndexOf("systemctl unmask postfix dovecot opendkim", StringComparison.Ordinal);
        Assert.True(unmask > 0 && unmask < script.IndexOf("systemctl enable postfix dovecot opendkim", StringComparison.Ordinal));
    }

    [Fact]
    public void Setup_marks_the_stack_provisioned_only_once_its_services_run()
    {
        // Tasks 23257 and 23258 (2026-10-03): the marker was written before enable, so a setup that died
        // there left the page saying "Provisionné par Aetheus" over a stopped Postfix.
        var script = LinuxHostConfigTemplates.Read("mail/mail-setup");
        Assert.True(script.IndexOf("/etc/aetheus/mail-stack.managed", StringComparison.Ordinal)
            > script.IndexOf("systemctl restart postfix", StringComparison.Ordinal));
    }

    [Fact]
    public void Purge_unmasks_the_units_before_purging_the_packages()
    {
        // Tasks 23257 and 23258 (2026-10-03): postfix was masked on the host. A unit still masked after
        // "Réinstaller la messagerie" would make the next setup's enable fail the same way.
        var purge = PurgeBlock();
        var unmask = purge.IndexOf("systemctl unmask \"$u.service\"", StringComparison.Ordinal);
        var aptPurge = purge.IndexOf("apt-get purge", StringComparison.Ordinal);
        Assert.True(unmask > 0, "purge never unmasks");
        Assert.True(aptPurge > unmask, "purge runs apt-get purge before unmasking");
        Assert.True(purge.IndexOf("systemctl disable --now", StringComparison.Ordinal) is var disable && disable > unmask && disable < aptPurge);
        Assert.Contains("postfix dovecot-core dovecot-imapd dovecot-pop3d dovecot-lmtpd opendkim opendkim-tools", purge, StringComparison.Ordinal);
        // Never autoremove: on a shared host it would take packages that have nothing to do with mail.
        Assert.DoesNotContain("autoremove", purge, StringComparison.Ordinal);
    }

    [Fact]
    public void Purge_removes_the_ownership_marker_last()
    {
        // The page reads "provisioned by Aetheus" from the marker: it goes only once everything else is gone,
        // so a purge that dies half-way is still offered again.
        var purge = PurgeBlock();
        Assert.Contains("marker=/etc/aetheus/mail-stack.managed", purge, StringComparison.Ordinal);
        var markerRemoval = purge.IndexOf("rm -f \"$marker\"", StringComparison.Ordinal);
        Assert.True(markerRemoval > 0, "purge never removes the ownership marker");
        Assert.True(markerRemoval > purge.LastIndexOf("rm -rf", StringComparison.Ordinal));
        Assert.True(markerRemoval > purge.IndexOf("userdel vmail", StringComparison.Ordinal));
        foreach (var path in new[] { "/etc/postfix", "/etc/dovecot", "/etc/opendkim", "/etc/opendkim.conf", "/var/mail/vhosts" })
            Assert.Contains(path, purge, StringComparison.Ordinal);
    }

    [Fact]
    public void Purge_only_acts_on_what_is_there_so_a_second_run_succeeds()
    {
        // Idempotent shape: packages dpkg does not know, units that do not exist and a vmail account that
        // is not mail-setup's (uid/gid 5000) are left alone, and every removal tolerates a missing path.
        var purge = PurgeBlock();
        Assert.True(purge.IndexOf("dpkg-query -W", StringComparison.Ordinal) is var query && query > 0
            && query < purge.IndexOf("apt-get purge", StringComparison.Ordinal));
        Assert.Contains("if [ -n \"$to_purge\" ]; then", purge, StringComparison.Ordinal);
        Assert.True(purge.IndexOf("systemctl list-unit-files", StringComparison.Ordinal) is var exists && exists > 0
            && exists < purge.IndexOf("systemctl disable --now", StringComparison.Ordinal));
        Assert.Contains("if [ \"$(id -u vmail 2>/dev/null || true)\" = 5000 ]; then userdel vmail; fi", purge, StringComparison.Ordinal);
        Assert.Contains("if [ \"$(getent group vmail | cut -d: -f3)\" = 5000 ]; then groupdel vmail; fi", purge, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"(^|\s)rm (?!-f |-rf )"), purge);
        Assert.DoesNotContain("eval", purge, StringComparison.Ordinal);
    }

    [Fact]
    public void Purge_removes_redis_only_when_spam_install_installed_it()
    {
        // A Redis the host already ran for another application must survive the mail purge.
        var script = LinuxHostConfigTemplates.Read("mail/mail-manage");
        Assert.Contains("for p in $installed_here; do printf '%s\\n' \"$p\"; done >> /etc/aetheus/mail-spam.packages", script, StringComparison.Ordinal);
        var purge = PurgeBlock();
        Assert.Matches(new Regex(
            @"if grep -qsx redis-server /etc/aetheus/mail-spam\.packages; then\n\s+units=""\$units redis-server""; packages=""\$packages redis-server""\n\s+fi"),
            purge);
        // Nowhere else does the purge name Redis.
        Assert.Equal(3, Regex.Matches(purge, "redis-server").Count);
    }

    [Fact]
    public void The_helper_announces_version_6_which_carries_purge_master_set_set_quota_and_retry_tuning()
    {
        // The page gates "Réinstaller la messagerie" on the reported helper version (3 = purge exists) and
        // "Préparer l'accès administrateur" on version 4 (recette R1-41: master-set exists) the mailbox quota on 5 (DEP-005) and the retry tuning on 6.
        var script = LinuxHostConfigTemplates.Read("mail/mail-manage");
        Assert.Contains("# aetheus-mail-helper-version: 6\n", script, StringComparison.Ordinal);
        Assert.Contains("\n  retry-tuning)\n", script, StringComparison.Ordinal);
        Assert.Contains("echo \"aetheus-mail-helper-version: 6\"", script, StringComparison.Ordinal);
        Assert.Contains("\n  purge)\n", script, StringComparison.Ordinal);
        Assert.Contains("\n  master-set)\n", script, StringComparison.Ordinal);
        Assert.Contains("\n  set-quota)\n", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_sets_the_shorter_retry_timings_and_stays_ipv4_only()
    {
        // 2026-10-20: Gmail defers the hosting provider's netblock (421-4.7.28); never below 60 s.
        var script = LinuxHostConfigTemplates.Read("mail/mail-setup");
        Assert.Contains("postconf -e \"minimal_backoff_time = 60s\"", script, StringComparison.Ordinal);
        Assert.Contains("postconf -e \"maximal_backoff_time = 900s\"", script, StringComparison.Ordinal);
        Assert.Contains("postconf -e \"queue_run_delay = 60s\"", script, StringComparison.Ordinal);
        Assert.Contains("postconf -e \"inet_protocols = ipv4\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Manage_retry_tuning_takes_no_argument_and_applies_the_same_three_values()
    {
        var script = LinuxHostConfigTemplates.Read("mail/mail-manage");
        var start = script.IndexOf("\n  retry-tuning)\n", StringComparison.Ordinal);
        Assert.True(start > 0);
        var block = script[start..script.IndexOf("\n    ;;\n", start, StringComparison.Ordinal)];
        Assert.Contains("[ \"$#\" -eq 1 ] || die \"retry-tuning takes no argument\"", block, StringComparison.Ordinal);
        Assert.Contains(
            "postconf -e \"minimal_backoff_time = 60s\" \"maximal_backoff_time = 900s\" \"queue_run_delay = 60s\"",
            block, StringComparison.Ordinal);
        Assert.Contains("systemctl reload postfix", block, StringComparison.Ordinal);
        // No variable can reach postconf: the block never expands a positional parameter.
        Assert.DoesNotContain("$2", block, StringComparison.Ordinal);
        Assert.DoesNotContain("${2", block, StringComparison.Ordinal);
    }

    // The `purge)` branch of mail-manage, up to its closing `;;`.
    private static string PurgeBlock()
    {
        var script = LinuxHostConfigTemplates.Read("mail/mail-manage");
        var start = script.IndexOf("\n  purge)\n", StringComparison.Ordinal);
        Assert.True(start > 0, "mail-manage has no purge sub-command");
        var end = script.IndexOf("\n    ;;\n", start, StringComparison.Ordinal);
        return script[start..end];
    }

    // Resolve a `$AETHEUS_HELPER_DIR/...`-shaped helper path var against the script's AETHEUS_HELPER_DIR.
    private static string ResolveHelperPath(string varName)
    {
        var script = ReadInstallScript();
        var helperDir = ResolveVar(script, "AETHEUS_HELPER_DIR");
        return ResolveVar(script, varName).Replace("$AETHEUS_HELPER_DIR", helperDir, StringComparison.Ordinal);
    }

    private static string ResolveVar(string script, string name)
    {
        var m = Regex.Match(script, $@"^{Regex.Escape(name)}=""([^""]*)""", RegexOptions.Multiline);
        Assert.True(m.Success, $"{name}=\"…\" not found in install-agent-linux.sh");
        return m.Groups[1].Value;
    }

    private static string ReadInstallScript()
        => File.ReadAllText(Path.Combine(FindRepoRoot(), "deploy", "agent-installer", "install-agent-linux.sh"));

    private static string FindRepoRoot() => Aetheus.Agent.Core.Tests.RepositoryScan.Root;
}
