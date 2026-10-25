// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Tests.Architecture;

public sealed class LinuxAgentInstallerHardeningAuditTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void HealthCheck_RequiresInitialCompatibilityHeartbeatBeforeSuccess()
    {
        var installer = ReadInstaller();

        Assert.Contains("Initial heartbeat sent; agent contract is ready", installer, StringComparison.Ordinal);
        Assert.Contains("_hc_enrolled", installer, StringComparison.Ordinal);
        Assert.Contains("_hc_contract_ready", installer, StringComparison.Ordinal);
        Assert.Matches(
            @"# Positive signals\.\s+if \[ \""\$_hc_enrolled\"" -eq 1 \] && \[ \""\$_hc_contract_ready\"" -eq 1 \]; then[\s\S]*?return 0\s+fi",
            installer);
    }

    [Fact]
    public void ServiceUnit_CreatesAgentFilesWithPrivateUmask()
    {
        // The unit is the agent/aetheus-agent.service template the installer renders.
        Assert.Contains(
            "render_host_config agent/aetheus-agent.service \"$SYSTEMD_UNIT_PATH\"",
            ReadInstaller(),
            StringComparison.Ordinal);
        Assert.Contains(
            "User=#{AGENT_USER}#\nGroup=#{AGENT_GROUP}#\nUMask=0077\nWorkingDirectory=#{INSTALL_DIR}#",
            LinuxHostConfigTemplates.Read("agent/aetheus-agent.service"),
            StringComparison.Ordinal);
    }

    /// <summary>The installer-rendered unit requires NoNewPrivileges=true when no sudo grant is
    /// enabled.</summary>
    [Fact]
    public void ServiceUnit_WithoutElevation_RendersNoNewPrivilegesTrue()
    {
        var installer = ReadInstaller().Replace("\r\n", "\n", StringComparison.Ordinal);
        var unitWriter = installer[installer.IndexOf("write_systemd_unit() {", StringComparison.Ordinal)..];
        var sandboxed = unitWriter[unitWriter.IndexOf("    else\n", StringComparison.Ordinal)..];
        sandboxed = sandboxed[..sandboxed.IndexOf("\n    fi\n", StringComparison.Ordinal)];
        var assignment = System.Text.RegularExpressions.Regex.Match(sandboxed, "NNP_BLOCK=\"([^\"]*)\"");
        Assert.True(assignment.Success, "the sandboxed branch of write_systemd_unit must set NNP_BLOCK");

        var rendered = LinuxHostConfigTemplates.Read("agent/aetheus-agent.service")
            .Replace("#{NNP_BLOCK}#", assignment.Groups[1].Value, StringComparison.Ordinal);

        Assert.Contains(
            "# --- Security hardening ---\n"
            + "# Fully sandboxed: no sudo grant, so privilege escalation is blocked outright.\n"
            + "NoNewPrivileges=true\n"
            + "#{PROTECT_SYSTEM_BLOCK}#\n",
            rendered,
            StringComparison.Ordinal);
        // The elevated branch is unchanged: it still relaxes the flag for the sudo grant.
        Assert.Contains("NoNewPrivileges=false\"", unitWriter, StringComparison.Ordinal);
    }

    [Fact]
    public void ApacheManage_PreservesSharedHostVhosts()
    {
        var installer = ReadInstaller();

        Assert.Contains("chmod +t \"$d\"", installer, StringComparison.Ordinal);
        Assert.Contains("setfacl -x \"d:u:$AGENT_USER\" \"$d\"", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("setfacl -d -m \"u:$AGENT_USER:rwx\"", installer, StringComparison.Ordinal);
        Assert.Contains("head -n 1 \"$f\" | grep -Fq '# Managed by Aetheus'", installer, StringComparison.Ordinal);
        Assert.Contains("chown -h \"$AGENT_USER:$AGENT_GROUP\" \"$enabled\"", installer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The typed blue-green switch invokes its reload helper as <c>sudo -n &lt;path&gt;</c> with argv
    /// exactly one element, so it cannot reach the three-word <c>systemctl reload</c> grant. This
    /// pins the dedicated helper: no arguments, a configtest that refuses to apply a rejected
    /// configuration (a reload that returns success without applying anything would report a cutover
    /// that never happened), and a sudoers entry naming its exact absolute path.
    /// </summary>
    [Fact]
    public void ApacheReloadHelper_ValidatesBeforeApplyingAndIsGrantedByExactPath()
    {
        var installer = ReadInstaller();
        // The helper and its sudoers drop-in are templates the installer renders.
        var helper = LinuxHostConfigTemplates.Read("apache/aetheus-apache-reload");
        var sudoers = LinuxHostConfigTemplates.Read("apache/sudoers.d/aetheus-apache");

        Assert.Contains(
            "APACHE_RELOAD_HELPER_PATH=\"/usr/local/lib/aetheus/aetheus-apache-reload\"",
            installer, StringComparison.Ordinal);
        Assert.Contains(
            "render_host_config apache/aetheus-apache-reload \"$APACHE_RELOAD_HELPER_PATH\"",
            installer, StringComparison.Ordinal);
        Assert.Contains(
            "[ \"$#\" -eq 0 ] || { echo \"aetheus-apache-reload accepts no arguments\" >&2; exit 2; }",
            helper, StringComparison.Ordinal);
        Assert.Contains(
            "/usr/sbin/apache2ctl configtest || { echo \"aetheus-apache-reload: configuration rejected; nothing applied\" >&2; exit 1; }",
            helper, StringComparison.Ordinal);
        Assert.Contains("exec /bin/systemctl reload apache2.service", helper, StringComparison.Ordinal);
        Assert.Contains("#{APACHE_RELOAD_HELPER_PATH}#", sudoers, StringComparison.Ordinal);
        Assert.Contains("chown root:root \"$APACHE_RELOAD_HELPER_PATH\"", installer, StringComparison.Ordinal);
        Assert.Contains("chmod 755 \"$APACHE_RELOAD_HELPER_PATH\"", installer, StringComparison.Ordinal);
        // A rejected sudoers file must leave no helper behind that nothing is allowed to invoke.
        Assert.Contains("rm -f \"$APACHE_RELOAD_HELPER_PATH\"", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void ApacheAndCertbot_KeepStrictSystemProtectionWithTargetedWritablePaths()
    {
        var installer = ReadInstaller();

        Assert.Contains("PROTECT_SYSTEM_BLOCK=\"ProtectSystem=strict\"", installer, StringComparison.Ordinal);
        Assert.Contains("READ_WRITE_PATHS_BLOCK=\"$READ_WRITE_PATHS_BLOCK /etc/apache2\"", installer, StringComparison.Ordinal);
        Assert.Contains(
            "mkdir -p /etc/letsencrypt /var/lib/letsencrypt /var/log/letsencrypt",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "/etc/letsencrypt /var/lib/letsencrypt /var/log/letsencrypt",
            installer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "package/mail/deploy/apache-manage/certbot-manage grants write system paths",
            installer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Homes_StayHidden_SaveTheSnapDataDirectoryCertbotNeeds()
    {
        // Snap certbot cannot start without /root/snap (nightly 2503, 2026-10-02); nothing wider opens.
        var installer = ReadInstaller();
        var unit = LinuxHostConfigTemplates.Read("agent/aetheus-agent.service");

        Assert.Contains("#{PROTECT_HOME_BLOCK}#", unit, StringComparison.Ordinal);
        Assert.Contains("PROTECT_HOME_BLOCK=\"ProtectHome=true\"", installer, StringComparison.Ordinal);
        Assert.Contains("PROTECT_HOME_BLOCK=\"ProtectHome=tmpfs\nBindPaths=-/root/snap\"", installer, StringComparison.Ordinal);
        // The self-update worker hides the homes too: a bare mkdir there aborted the upgrade (2026-10-03).
        Assert.Contains("if ! mkdir -p /root/snap 2>/dev/null; then", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("\n        mkdir -p /root/snap\n", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectHome=false", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectHome=read-only", installer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The effective home rule once the SSH binds are added: `ProtectHome=tmpfs` exists from systemd 242
    /// only, an older systemd ignores the value and leaves the homes readable. The installer therefore
    /// switches to tmpfs and binds the .ssh directories only behind a version test, and an unreadable
    /// version counts as too old.
    /// </summary>
    [Fact]
    public void SshBinds_TurnTheHomesIntoTmpfsOnlyBehindTheSystemdVersionCheck()
    {
        var installer = ReadInstaller();

        // The default is still the full protection; tmpfs is written by exactly two places: the certbot
        // opt-in (pre-existing) and the SSH binds below.
        Assert.Contains("PROTECT_HOME_BLOCK=\"ProtectHome=true\"", installer, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(installer, "PROTECT_HOME_BLOCK=\"ProtectHome=tmpfs").Count);

        var versionCheck = installer.IndexOf("if [ \"$(systemd_major_version)\" -ge 242 ]; then", StringComparison.Ordinal);
        var fill = installer.IndexOf("_ssh_bind=\"$_ssh_bind -$_ssh_dir\"", StringComparison.Ordinal);
        var useBinds = installer.IndexOf("if [ -n \"$_ssh_bind\" ]; then", StringComparison.Ordinal);
        var tmpfs = installer.IndexOf("PROTECT_HOME_BLOCK=\"ProtectHome=tmpfs\"\n", StringComparison.Ordinal);
        var bind = installer.IndexOf("BindReadOnlyPaths=${_ssh_bind# }", StringComparison.Ordinal);
        Assert.True(versionCheck > 0, "the systemd version test is missing");
        Assert.True(versionCheck < fill && fill < useBinds, "the binds are built outside the version test");
        Assert.True(useBinds < tmpfs && tmpfs < bind, "tmpfs and the binds are set before the binds exist");
        Assert.Contains("[ -n \"$_ssh_bind\" ]", installer, StringComparison.Ordinal);

        // An unreadable version is 0, so it is treated as too old and the homes keep ProtectHome=true.
        Assert.Contains("systemd_major_version() {", installer, StringComparison.Ordinal);
        Assert.Contains("\"\"|*[!0-9]*) echo 0 ;;", installer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent reads the kernel messages (OOM victim name) and the SSH configuration with read-only
    /// rights: the journal group and read ACLs. Neither is allowed to become a capability, a kernel-log
    /// write path or a write grant (docs/contracts/security.md, "Read-only host observation rights").
    /// </summary>
    [Fact]
    public void ReadOnlyObservation_GrantsReadRightsOnly_AndKeepsTheKernelLogProtection()
    {
        var installer = ReadInstaller();
        var unit = LinuxHostConfigTemplates.Read("agent/aetheus-agent.service");
        var helper = LinuxHostConfigTemplates.Read("agent/aetheus-agent-read-grant");

        // Kernel messages come from the journal group; ProtectKernelLogs stays on and no capability is added.
        Assert.Contains("ProtectKernelLogs=true", unit, StringComparison.Ordinal);
        Assert.Contains("#{JOURNAL_GROUP_BLOCK}#", unit, StringComparison.Ordinal);
        Assert.Contains("SupplementaryGroups=systemd-journal", installer, StringComparison.Ordinal);
        foreach (var text in new[] { unit, installer, helper })
        {
            Assert.DoesNotContain("ProtectKernelLogs=false", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CAP_SYSLOG", text, StringComparison.Ordinal);
            Assert.DoesNotContain("AmbientCapabilities", text, StringComparison.Ordinal);
        }

        // The ACL helper runs as root from a unit of its own (a bind mount of the agent unit would make the
        // files read-only for setfacl, even under the '+' prefix), its failure never stops the agent (Wants=,
        // not Requires=), and the installer renders it root-owned and not writable by anyone else.
        var grantUnit = LinuxHostConfigTemplates.Read("agent/aetheus-agent-read-grant.service");
        Assert.Contains("Wants=network-online.target aetheus-agent-read-grant.service", unit, StringComparison.Ordinal);
        Assert.Contains("After=network-online.target aetheus-agent-read-grant.service", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("Requires=aetheus-agent-read-grant", unit, StringComparison.Ordinal);
        Assert.Contains("Before=aetheus-agent.service", grantUnit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=#{AGENT_READ_GRANT_HELPER_PATH}#", grantUnit, StringComparison.Ordinal);
        Assert.Contains("NoNewPrivileges=true", grantUnit, StringComparison.Ordinal);
        // Root is held to reading any file and writing the ACL of a file it does not own, nothing else.
        var grantSettings = grantUnit.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && line[0] != '#').ToList();
        Assert.Contains("CapabilityBoundingSet=CAP_FOWNER CAP_DAC_READ_SEARCH", grantSettings);
        Assert.DoesNotContain(grantSettings, line => line.Contains("CAP_DAC_OVERRIDE", StringComparison.Ordinal));
        Assert.Contains(
            "render_host_config agent/aetheus-agent-read-grant \"$AGENT_READ_GRANT_HELPER_PATH\"",
            installer, StringComparison.Ordinal);
        Assert.Contains(
            "render_host_config agent/aetheus-agent-read-grant.service \"$AGENT_READ_GRANT_SERVICE_PATH\"",
            installer, StringComparison.Ordinal);
        Assert.Contains("chown root:root \"$AGENT_READ_GRANT_HELPER_PATH\"", installer, StringComparison.Ordinal);
        Assert.Contains("chmod 755 \"$AGENT_READ_GRANT_HELPER_PATH\"", installer, StringComparison.Ordinal);

        // Every grant it makes is a read (r) or a directory traverse (x), nothing else, through setfacl on
        // the open file.
        var grants = helper.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("change \"$ACTION\"", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(3, grants.Count);
        Assert.All(grants, line => Assert.Matches(@" [rx]$", line));
        Assert.Contains("setfacl -m \"u:$AGENT:$5\" /proc/self/fd/3", helper, StringComparison.Ordinal);

        // Where the homes are an empty tmpfs, only the .ssh directories are bound in, read-only.
        Assert.Contains("BindReadOnlyPaths=${_ssh_bind# }", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("BindPaths=${_ssh_bind", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void MailManage_UsesLiteralKeyFilteringAndOwnsBothDkimOutputs()
    {
        // The helper is the mail/mail-manage template the installer renders.
        Assert.Contains(
            "render_host_config mail/mail-manage \"$MAIL_MANAGE_HELPER_PATH\"",
            ReadInstaller(),
            StringComparison.Ordinal);
        var installer = LinuxHostConfigTemplates.Read("mail/mail-manage");

        Assert.Contains("without_space_key() { awk -v key=\"$1\" '$1 != key'", installer, StringComparison.Ordinal);
        Assert.Contains("without_colon_key() { awk -F: -v key=\"$1\" '$1 != key'", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("grep -v \"^$email ", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("grep -v \"^$email:", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("grep -v \"^\\*@$domain ", installer, StringComparison.Ordinal);
        // DKIM keys are scoped by domain and owned by opendkim, with private mode 600 and public
        // mode 644 for agent inventory.
        Assert.Contains("chown -R opendkim:opendkim \"/etc/opendkim/keys/$d\"", installer, StringComparison.Ordinal);
        Assert.Contains("chmod 600 \"/etc/opendkim/keys/$d/$s.private\"", installer, StringComparison.Ordinal);
        Assert.Contains("chmod 644 \"/etc/opendkim/keys/$d/$s.txt\"", installer, StringComparison.Ordinal);
    }

    private static string ReadInstaller() =>
        File.ReadAllText(Path.Combine(RepoRoot, "deploy", "agent-installer", "install-agent-linux.sh"));

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;
}
