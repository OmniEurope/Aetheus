// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Tasks;

/// <summary>
/// F-32: Typed operations the agent can execute without free-form shell strings.
/// Each value maps to a dedicated <c>IOperationExecutor</c> on the agent that uses
/// <c>ProcessStartInfo.ArgumentList</c> directly - no shell interpolation, no allow-list regex.
/// Free-form pipeline scripts continue to use <c>IExecutor</c> (the escape hatch).
/// </summary>
public enum OperationKind
{
    None = 0,

    // Docker
    DockerRestartContainer = 100,
    DockerStartContainer = 101,
    DockerStopContainer = 102,
    DockerPullImage = 103,

    /// <summary>
    /// BACK-012: deploy a Compose stack from a user-supplied file. Target is the Compose project name
    /// (<c>ComposeProjectRegex</c>); the base64 file travels in <c>AETHEUS_DOCKER_COMPOSE_B64</c>. The
    /// agent writes it under its work directory (one folder per stack) and runs <c>docker compose -f &lt;file&gt; -p &lt;stack&gt; up -d</c>
    /// as an argv - no shell, no pipe for the command validator to refuse.
    /// </summary>
    DockerComposeUp = 104,

    /// <summary>
    /// BACK-012: run a user-supplied script inside a container. Target is the container ID; the
    /// base64 script travels in <c>AETHEUS_DOCKER_EXEC_B64</c> and is written to the stdin of
    /// <c>docker exec -i &lt;id&gt; sh</c>, so its text never reaches a host shell.
    /// </summary>
    DockerExecScript = 105,

    /// <summary>
    /// BACK-012: build an image from a user-supplied Dockerfile. Target is the image tag; the base64
    /// Dockerfile travels in <c>AETHEUS_DOCKER_DOCKERFILE_B64</c>. The agent writes it to a fresh temp
    /// directory that is also the build context (empty but for the Dockerfile) and runs
    /// <c>docker build -t &lt;tag&gt; -f &lt;dir&gt;/Dockerfile &lt;dir&gt;</c>.
    /// </summary>
    DockerBuildImage = 106,

    // systemd / service
    ServiceStart = 200,
    ServiceStop = 201,
    ServiceRestart = 202,
    ServiceStatus = 203,

    /// <summary>
    /// Read the last N journal lines of a systemd unit via <c>journalctl -u &lt;unit&gt; -n &lt;lines&gt;
    /// --no-pager [-f]</c> (argv-only, no shell interpolation - replaces the former shell-string logs
    /// task). Target is the unit name (<c>ServiceNameRegex</c>); line count and follow flag ride in
    /// <see cref="Aetheus.Shared.Components.Shared.ServiceLogsEnv"/>. No sudo (journal read).
    /// </summary>
    ServiceGetLogs = 205,

    /// <summary>
    /// Phase 3 (option B): enable a systemd unit (start-on-boot) via the controlled-sudo recipe.
    /// Target is the unit name (validated against <c>ServiceNameRegex</c>); the agent normalises a
    /// trailing <c>.service</c> and invokes <c>sudo -n /bin/systemctl enable &lt;unit&gt;</c>. The
    /// argv-exact <c>/etc/sudoers.d/aetheus-service-enable</c> drop-in bounds it to a FIXED list
    /// of pre-existing units (no wildcards), satisfying the security doc's "operate only on a fixed
    /// list of pre-existing units" exception to the otherwise-forbidden <c>systemctl enable</c>.
    /// </summary>
    ServiceEnable = 204,

    /// <summary>
    /// S-FEAT-W8KN: install an OS package on a managed server via the controlled-sudo recipe. Target
    /// is the package name (validated against the closed <see cref="Aetheus.Shared.Components.Servers.ManageablePackages"/>
    /// allow-list); the agent invokes <c>sudo -n /usr/bin/apt-get install -y &lt;pkg&gt;</c> against the
    /// argv-exact <c>/etc/sudoers.d/aetheus-package</c> drop-in (a package outside the list is
    /// refused by sudo at the OS level). Requires the server-management module's package-manage capability.
    /// </summary>
    ServiceInstall = 210,

    /// <summary>Uninstall an OS package with sudo apt-get purge under the same allow-list and exact
    /// sudoers grant as <see cref="ServiceInstall"/>.</summary>
    ServiceUninstall = 211,

    /// <summary>
    /// ADR-024 4.1: apply pending OS security/package updates via the controlled-sudo recipe. Runs in
    /// two modes carried in <c>AETHEUS_PATCH_DRY_RUN</c>: a non-mutating dry-run (<c>apt-get -s upgrade</c>,
    /// no sudo, lists what would change) and an apply (<c>sudo -n /usr/bin/apt-get -y upgrade</c> against
    /// the argv-exact <c>/etc/sudoers.d/aetheus-patch</c> drop-in). Before applying, the agent runs the
    /// dry-run and ABORTS honestly if any <see cref="Aetheus.Shared.Components.Tasks.CriticalPackages"/> (or a
    /// per-server override) would be upgraded - never a fake success. Target is unused. Requires the
    /// server-management module's patch-manage capability.
    /// </summary>
    SystemPackageUpgrade = 212,

    /// <summary>Recette R3-59: the read-only list of every package installed on the host, read by the agent
    /// itself without privilege (<c>dpkg-query -W</c>, or <c>rpm -qa</c> where dpkg is absent). One
    /// <c>PACKAGE name version</c> line each, then <c>TOTAL n</c> (<see
    /// cref="Aetheus.Shared.Components.Servers.InstalledPackages"/>). Nothing is installed or removed:
    /// uninstalling an arbitrary package stays refused (no sudo rule without a fixed list). Target
    /// <c>"-"</c>. Gated by the <c>package.inventory</c> software capability, not by the package grant.</summary>
    PackageInventory = 213,

    // Apache
    ApacheReload = 300,
    ApacheTestConfig = 301,

    /// <summary>
    /// Item #8 of the plan: typed read of the apache error/access log. Target is the log type
    /// (<c>"error"</c> or <c>"access"</c>). Lines count is fixed at the agent (100) - the
    /// front-side preferences flow through the env vars, not the target string, so the
    /// operation-target validator can keep its short whitelist.
    /// </summary>
    ApacheGetLogs = 302,

    /// <summary>Item #5.2/#5.3 - service control via the controlled-sudo recipe (item #6).
    /// Target is unused; the executor invokes the matching <c>systemctl</c> verb on
    /// <c>apache2.service</c> through sudo with argv exact (no wildcards).</summary>
    ApacheStart = 303,
    ApacheStop = 304,
    ApacheRestart = 305,

    /// <summary>Item #5.3 - write a vhost config file via the ACL +rw the install script
    /// grants on <c>/etc/apache2/sites-available</c> (no sudo). Target is the site filename
    /// (validated against <c>SiteNameRegex</c>); the base64-encoded content is passed via the
    /// <c>AETHEUS_APACHE_CONFIG_B64</c> environment variable to keep the task command field
    /// short and free of shell metacharacters.</summary>
    ApacheSaveConfig = 306,

    /// <summary>type: apache-proxy - composite reverse-proxy apply: write the rendered vhost to
    /// <c>sites-available/&lt;site&gt;.conf</c> (ACL +rw, no sudo), enable it by symlinking into
    /// <c>sites-enabled</c>, then apply via <c>systemctl reload apache2.service</c> (controlled-sudo),
    /// which reloads AND validates - a broken vhost fails the reload while the old config stays live.
    /// (The controlled-sudo <c>noexec</c> blocks a standalone <c>apache2ctl</c> wrapper run, so the
    /// systemd reload is the validation point.) Target is the site filename; the base64 vhost is passed
    /// in <c>AETHEUS_APACHE_CONFIG_B64</c>. Requires <c>--enable-apache-manage</c>.</summary>
    ApacheConfigureProxy = 307,

    /// <summary>Typed read of a vhost config file. Target is the site filename (validated against
    /// <c>ApacheSiteFileRegex</c>); the config root travels in <c>AETHEUS_APACHE_CONFIG_ROOT</c>.
    /// The agent reads <c>{root}/sites-available/{site}</c> (or <c>{root}/conf.d/{site}</c>) directly via
    /// <c>File.ReadAllText</c> (read ACL, no shell) - replaces the dead <c>cat "..." 2&gt;/dev/null || cat</c>
    /// builder the CommandValidator rejected.</summary>
    ApacheGetConfig = 308,

    /// <summary>Typed read of a document root's <c>.htaccess</c>. Target is the absolute document root
    /// (validated against <c>ApacheDocumentRootRegex</c>); the agent reads <c>{root}/.htaccess</c> directly
    /// via <c>File.ReadAllText</c> (empty when absent) - replaces the dead <c>cat "..." || echo ''</c>
    /// builder.</summary>
    ApacheGetHtaccess = 309,

    /// <summary>Typed write of a document root's <c>.htaccess</c>. Target is the absolute document root;
    /// the base64 content travels in <c>AETHEUS_APACHE_HTACCESS_B64</c>. The agent writes
    /// <c>{root}/.htaccess</c> via <c>File.WriteAllBytes</c> (same non-sudo access model as the old shell,
    /// minus the metacharacters) - replaces the dead <c>printf ... | base64 -d &gt; "..."</c> builder.</summary>
    ApacheSaveHtaccess = 310,

    /// <summary>Atomically applies a bounded set of rendered Apache configuration files. Target is
    /// the fixed literal <c>config-set</c>; the encrypted task environment carries a base64 JSON map
    /// of safe destination filenames to base64 content. The agent snapshots, writes, enables,
    /// config-tests, reloads and restores the whole set on failure.</summary>
    ApacheApplyConfigSet = 311,

    /// <summary>Recette R-12: enables a site by linking <c>sites-enabled/&lt;site&gt;</c> to the regular file
    /// <c>sites-available/&lt;site&gt;</c> (sticky directory ACL, no sudo, no <c>a2ensite</c>), then runs the
    /// fixed configtest helper and <c>systemctl reload apache2.service</c> (controlled-sudo); the link is
    /// removed again when either fails. Target is the site filename (<c>ApacheSiteFileRegex</c>).</summary>
    ApacheEnableSite = 312,

    /// <summary>Recette R-12: disables a site by removing its <c>sites-enabled</c> link (only a link that
    /// points to the <c>sites-available</c> file of the same name), then configtest and reload; the link is put
    /// back when either fails. Target is the site filename.</summary>
    ApacheDisableSite = 313,

    /// <summary>Recette R-12: deletes the <c>sites-available</c> file of a DISABLED site (refused while any
    /// <c>sites-enabled</c> entry points to it), then configtest; the file is written back when Apache rejects
    /// the result. Target is the site filename.</summary>
    ApacheDeleteSite = 314,

    /// <summary>type: certbot - obtain/install an HTTPS certificate for one or more domains via a
    /// root-owned controlled-sudo helper (<c>sudo -n /usr/local/lib/aetheus/aetheus-certbot-issue</c>,
    /// which re-validates the domains then runs <c>certbot certonly --apache</c>). When ACME validation
    /// cannot complete (no public DNS / unreachable :80) production fails honestly; only explicit
    /// local mode creates a self-signed certificate in
    /// the Let's Encrypt layout so the site still serves HTTPS. Domains/email are passed via
    /// <c>AETHEUS_CERTBOT_*</c> env vars. Requires <c>--enable-certbot-manage</c>.</summary>
    CertbotObtain = 320,

    /// <summary>type: certbot management - renew a single certificate by lineage name via the root-owned
    /// controlled-sudo helper (<c>sudo -n /usr/local/lib/aetheus/aetheus-certbot-manage renew &lt;name&gt;</c>,
    /// which re-validates the name then runs <c>certbot renew --cert-name &lt;name&gt;</c>). Target is the
    /// certificate name. Requires <c>--enable-certbot-manage</c>. Replaces the old free-form
    /// <c>sudo certbot renew</c> shell task the agent's CommandValidator rejected (GTFOBins).</summary>
    CertbotRenew = 321,

    /// <summary>Renew ALL certificates - the helper runs <c>certbot renew</c>. Target unused. Same helper
    /// and sudoers grant as <see cref="CertbotRenew"/>.</summary>
    CertbotRenewAll = 322,

    /// <summary>Delete a certificate by lineage name - the helper runs <c>certbot delete --cert-name
    /// &lt;name&gt;</c>. Target is the certificate name.</summary>
    CertbotDelete = 323,

    /// <summary>Revoke then delete a certificate by lineage name - the helper runs <c>certbot revoke
    /// --cert-name &lt;name&gt; --delete-after-revoke</c>. Target is the certificate name.</summary>
    CertbotRevoke = 324,

    CertbotNormalize = 325,    // Reconfigure off-convention Certbot lineages to webroot; target is unused.
    CertbotRenewalCheck = 326, // Rehearse Certbot renewal with dry-run and retain the result for collection; target is unused.

    /// <summary>Recette R-12: reissues a certificate WITHOUT one of its names - the helper runs
    /// <c>certbot certonly --cert-name &lt;name&gt; -d &lt;each remaining name&gt;</c> through the ACME web root
    /// (<c>sudo -n aetheus-certbot-manage remove-name &lt;name&gt; &lt;domain&gt;</c>). The certificate itself is
    /// never deleted. Target is the certificate name; the name to take out travels in
    /// <c>AETHEUS_CERTBOT_REMOVE_DOMAIN</c> and reaches the helper as its third argument.</summary>
    CertbotRemoveDomain = 327,

    // RKHunter (items #10.2/#10.3) - same controlled-sudo recipe as Apache. Target unused.
    RkhunterScan = 350,        // sudo aetheus-rkhunter check (helper runs rkhunter --check --skip-keypress --nocolors --report-warnings-only)
    RkhunterUpdate = 351,      // sudo aetheus-rkhunter update (rkhunter --update --nocolors)
    RkhunterPropupd = 352,     // sudo aetheus-rkhunter propupd (rkhunter --propupd --nocolors)

    // Agent self-management
    /// <summary>
    /// The agent downloads the latest agent build from the backend's <c>/downloads</c> endpoint
    /// and hands off to a detached platform updater (systemd unit / Windows Service) that swaps
    /// the binaries and restarts the service. The target string is unused. Handled by
    /// <c>AgentSelfUpdateOperationExecutor</c> in <c>Aetheus.Agent.Core</c>.
    /// </summary>
    AgentSelfUpdate = 400,

    // Route mail service, queue and configuration operations through the root-owned mail-manage
    // helper.
    MailStartPostfix = 500,
    MailStopPostfix = 501,
    MailRestartPostfix = 502,
    MailReloadPostfix = 503,
    MailStartDovecot = 504,
    MailStopDovecot = 505,
    MailRestartDovecot = 506,
    MailReloadDovecot = 507,
    MailFlushQueue = 508,
    MailViewQueue = 509,
    MailTestConfig = 510,

    /// <summary>
    /// Typed read of mail logs via <c>mail-manage logs</c> (journal read as root). Target is the unit
    /// (<c>postfix</c>, <c>dovecot</c>, <c>opendkim</c> or <c>rspamd</c>); line count and optional literal
    /// filter ride in <c>AETHEUS_MAIL_LOG_*</c> env vars.
    /// </summary>
    MailGetLogs = 511,

    /// <summary>Set up postfix/dovecot/opendkim via the root-owned mail-setup helper and its exact
    /// sudoers grant. Target is the domain; settings are re-validated from env vars and the admin
    /// password travels over stdin, never argv. Requires the mail-setup capability.</summary>
    MailSetup = 512,

    /// <summary>Add a validated virtual domain via the root-owned mail-manage helper and the
    /// exact mail sudoers grant. Target is the new domain; no secret is passed.</summary>
    MailAddDomain = 513,

    /// <summary>Add a mailbox through mail-manage. Target is its email; domain and quota come
    /// from validated env vars. The password reaches the validating helper over stdin, never argv,
    /// and is hashed by doveadm.</summary>
    MailAddAccount = 514,

    /// <summary>
    /// S-FEAT-W8KN (incremental): add a virtual alias via the <c>mail-manage</c> helper (sub-command
    /// <c>add-alias</c>). Target is the source email; the destination travels in a
    /// <c>AETHEUS_MAIL_*</c> env var. No secret involved.
    /// </summary>
    MailAddAlias = 515,

    /// <summary>
    /// S-FEAT-W8KN (incremental): rotate the DKIM key via the <c>mail-manage</c> helper (sub-command
    /// <c>dkim-rotate</c>). Target is the domain; the new selector travels in a <c>AETHEUS_MAIL_*</c>
    /// env var. The helper regenerates the key, rewrites KeyTable/SigningTable, restarts opendkim, and
    /// emits the public-key TXT record on stdout. No secret involved.
    /// </summary>
    MailDkimRotate = 516,

    /// <summary>Change a mailbox password via mail-manage. Target is its email; the validated
    /// password travels over stdin, never argv, and the helper hashes it with doveadm.</summary>
    MailChangePassword = 517,

    /// <summary>Remove a mail domain via the root-owned <c>mail-manage</c> helper (<c>remove-domain
    /// &lt;domain&gt;</c>). Target is the domain. Replaces the dead shell builder whose
    /// <c>$(postconf ...) &amp;&amp;</c> chain the CommandValidator rejected.</summary>
    MailRemoveDomain = 518,

    /// <summary>Delete a mailbox account via <c>mail-manage delete-account &lt;email&gt; &lt;domain&gt;</c>.
    /// Target is the account email; the domain travels in <c>AETHEUS_MAIL_DOMAIN</c>. Replaces the dead
    /// <c>sed -i ... &amp;&amp; postmap ...</c> shell builder.</summary>
    MailDeleteAccount = 519,

    /// <summary>Remove a mail alias via <c>mail-manage remove-alias &lt;source&gt;</c>. Target is the
    /// alias source email. Replaces the dead <c>sed -i ... &amp;&amp; postmap ...</c> shell builder.</summary>
    MailRemoveAlias = 520,

    /// <summary>Read a domain's DKIM public key (DNS TXT record) via <c>mail-manage dkim-read &lt;domain&gt;
    /// &lt;selector&gt;</c> (per-domain key, legacy flat key as fallback). Target is the domain; the selector
    /// rides in <c>AETHEUS_MAIL_DKIM_SELECTOR</c>.</summary>
    MailDkimRead = 521,

    // Mail helper operations use the aetheus-mail grant and helper-side validation.
    /// <summary>Point Postfix and Dovecot at <c>/etc/letsencrypt/live/&lt;hostname&gt;</c> and install the
    /// renewal reload hook (<c>mail-manage install-cert</c>). Target is the mail hostname.</summary>
    MailInstallCertificate = 522,

    /// <summary>Install rspamd + Redis and chain the rspamd milter after OpenDKIM. Target <c>"-"</c>.</summary>
    MailSpamInstall = 523,

    /// <summary>Write the rspamd reject / add_header / greylist thresholds. Target <c>"-"</c>; scores in
    /// <c>AETHEUS_MAIL_SPAM_*</c> env vars.</summary>
    MailSpamConfigure = 524,

    /// <summary>Train the rspamd Bayes classifier. Target <c>"spam"</c> or <c>"ham"</c>; the message rides in
    /// an env var and reaches the helper over stdin.</summary>
    MailSpamLearn = 525,

    /// <summary>systemctl start/stop/restart/reload of postfix, dovecot, opendkim or rspamd through the
    /// helper. Target <c>unit:action</c>, for example <c>rspamd:restart</c>.</summary>
    MailServiceControl = 526,

    /// <summary>Send a tagged test message and follow its queue id to a final status line
    /// (<c>DELIVERY status qid token detail</c>). Target is the recipient; the sender is in an env var.</summary>
    MailSendTest = 527,

    /// <summary>Delete one queued message (<c>postsuper -d</c>). Target is the Postfix queue id.</summary>
    MailQueueDelete = 528,

    /// <summary>Report the Maildir size of every virtual mailbox (<c>QUOTA email kib</c> lines). Target <c>"-"</c>.</summary>
    MailQuotaReport = 529,

    /// <summary>Purge the mail stack services, packages, configuration, mailboxes, DKIM keys and
    /// ownership marker through mail-manage purge; target is "-".</summary>
    MailPurge = 530,

    /// <summary>Whether the server can open an outbound SMTP connection (TCP port 25), a
    /// prerequisite of sending mail that many hosting providers block. The agent connects to the
    /// target host on port 25 itself (no helper, no privilege), reads the greeting and prints one
    /// <c>CHECK outbound-25</c> line. Target is the host name to reach. Outside the mail-manage
    /// range on purpose: it needs only an agent recent enough to publish
    /// <c>mail.outbound-probe</c>.</summary>
    MailOutboundProbe = 531,

    /// <summary>Recette R1-41: (re)creates the Dovecot master user Aetheus opens mailboxes with
    /// (<c>mail-manage master-set</c>, helper version 4): its hashed password in
    /// <c>/etc/dovecot/master-users</c>, the master passdb and <c>auth_master_user_separator</c>, then a
    /// Dovecot reload. Target <c>"-"</c>; the password travels in <c>AETHEUS_MAIL_MASTER_PASSWORD</c>
    /// (stdin). Same mail-manage capability as the other mail kinds.</summary>
    MailMasterAccess = 532,

    /// <summary>Recette R3-23 / R3-26: the read-only check that nothing of a mail stack is left after an
    /// uninstall or a reinstall: the mail packages dpkg still knows (installed or with configuration files
    /// left, <c>rc</c>), the configuration and data directories, the Aetheus ownership marker, and the mail
    /// ports still listening. The agent reads them itself (no helper, no privilege) and prints one
    /// <c>LEFTOVER kind name detail</c> line each, then <c>CLEAN yes|no</c>. Target <c>"-"</c>. Gated by the
    /// <c>mail.leftovers-check</c> software capability, like <see cref="MailOutboundProbe"/>.</summary>
    MailLeftoversCheck = 533,

    /// <summary>DEP-005: writes the mailbox quota Aetheus stores (MiB, 0 = unlimited) on the account's
    /// Dovecot users-file line (<c>mail-manage set-quota</c>), where the Dovecot quota plugin enforces it
    /// on IMAP APPEND and LMTP delivery. Target is the address; the quota travels in
    /// <c>AETHEUS_MAIL_QUOTA_MB</c>. Same mail-manage capability as the other mail kinds.</summary>
    MailSetQuota = 534,

    /// <summary>Applies the shorter Postfix retry timings (<c>minimal_backoff_time</c> 60s,
    /// <c>maximal_backoff_time</c> 900s, <c>queue_run_delay</c> 60s) on an already provisioned host through
    /// <c>mail-manage retry-tuning</c>. Target <c>"-"</c>; the values are fixed in the helper, never an input.
    /// Same mail-manage capability as the other mail kinds.</summary>
    MailRetryTuning = 535,

    // Portsentry (service control + status + unblock)
    PortsentryStart = 550,
    PortsentryStop = 551,
    PortsentryRestart = 552,
    PortsentryStatus = 553,

    /// <summary>
    /// Typed read of portsentry logs via <c>journalctl</c>. Target is unused.
    /// </summary>
    PortsentryGetLogs = 554,

    /// <summary>Provision/configure portsentry via the root-owned <c>portsentry-setup</c> helper
    /// (<c>sudo -n</c>, argv-exact): apt-installs portsentry, writes the scan mode into
    /// <c>/etc/default/portsentry</c> and the TCP/UDP port lists into <c>portsentry.conf</c>, then enables
    /// the service. Target is the scan mode; the port lists travel in <c>AETHEUS_PORTSENTRY_*_PORTS</c>
    /// env vars. Replaces the dead <c>sed ... 2&gt;/dev/null || true &amp;&amp; apt-get install</c> shell chain
    /// the CommandValidator rejected. Requires the portsentry-manage capability.</summary>
    PortsentrySetup = 556,

    /// <summary>
    /// Unblock an IP address from portsentry. Target is the IPv4/IPv6 address to unblock,
    /// validated by <see cref="Aetheus.Shared.Components.Shared.OperationTargetValidator"/>.
    /// </summary>
    PortsentryUnblock = 555,

    // Firewall (ADR-024 4.2) - ufw control via the root-owned aetheus-firewall helper (sudo -n, the
    // helper re-validates port/proto/source before running ufw). Complements Portsentry (detection) with
    // control (prevention). Requires the firewall-manage capability. Anti-lockout is a LOGIC guard: a deny
    // on the admin SSH port is refused, and the admin port is auto-allowed before ufw is enabled.
    /// <summary>Open a port: <c>ufw allow</c>. Target is the port (1-65535); protocol (tcp/udp) and source
    /// (CIDR or "any") travel in <c>AETHEUS_FIREWALL_*</c> env vars, re-validated agent- and helper-side.</summary>
    FirewallAllow = 560,

    /// <summary>Close/deny a port: <c>ufw deny</c>. Same target/env shape as <see cref="FirewallAllow"/>.
    /// Refused (anti-lockout) when it targets the admin SSH port.</summary>
    FirewallDeny = 561,

    /// <summary>Delete an existing rule: <c>ufw delete</c> the matching allow/deny. Same target/env shape.</summary>
    FirewallDeleteRule = 562,

    /// <summary>Enable or disable ufw. Target is <c>"enable"</c> or <c>"disable"</c>. On enable the helper
    /// first allows the admin SSH port so the default-deny policy can't lock the operator out.</summary>
    FirewallSetEnabled = 563,

    // Observe host TCP listeners without privilege and report them to the registry. Use ss or
    // Get-NetTCPConnection through argv-only execution; no target is required.
    /// <summary>Scan the host's listening TCP ports on demand and report them as <c>Observed</c>
    /// reservations. Target is empty. Requires the <c>ports.observe</c> capability.</summary>
    PortsObserve = 570,

    // Pipeline artifacts & releases
    /// <summary>
    /// Collect build artifacts from the agent's working directory. The <c>Command</c> field
    /// contains a JSON-serialized list of glob patterns to include in the zip. Environment
    /// variables carry <c>AETHEUS_ARTIFACT_NAME</c>, <c>AETHEUS_RUN_ID</c>, and
    /// <c>AETHEUS_STAGE_NAME</c>.
    /// </summary>
    PipelineCollectArtifacts = 600,

    /// <summary>
    /// Execute a release step: generate changelog from git log and POST release to backend.
    /// <c>Command</c> contains the version pattern. Environment variables carry
    /// <c>AETHEUS_PROJECT_ID</c>, <c>AETHEUS_RUN_ID</c>, <c>AETHEUS_CHANGELOG</c> (bool).
    /// </summary>
    PipelineCreateRelease = 601,

    /// <summary>
    /// Replace <c>#{VAR_NAME}#</c> tokens in target files with resolved pipeline variable values.
    /// <c>Command</c> is a JSON array of file glob patterns. All resolved pipeline variables
    /// are available in the environment variables.
    /// </summary>
    PipelineSubstituteVariables = 602,

    /// <summary>
    /// Publish Cobertura-format coverage XML to the backend. <c>Command</c> is a JSON array of
    /// glob patterns to find coverage XML files (e.g. <c>["**/coverage.cobertura.xml"]</c>).
    /// </summary>
    PipelinePublishCoverage = 603,

    /// <summary>
    /// Publish a SARIF lint report to the backend. <c>Command</c> is a JSON array of glob patterns
    /// to find SARIF files (e.g. <c>["**/*.sarif"]</c>). Counts are stored as a structured lint result.
    /// </summary>
    PipelinePublishLint = 604,

    /// <summary>
    /// L: analyze C# cyclomatic complexity + LOC over the workspace (Roslyn) and publish aggregate
    /// metrics. <c>Command</c> is unused; the agent walks the working directory for <c>*.cs</c> files.
    /// </summary>
    PipelinePublishComplexity = 605,

    /// <summary>
    /// Cross-agent deployment: apply a build artifact (or an existing release) on a deployment-capable
    /// agent. The target is the application instance name (validated against
    /// <see cref="Aetheus.Shared.Components.Shared.OperationTargetValidator"/>'s deploy-app regex). The
    /// resolved artifact is pulled from the backend's artifact store via an agent-authorised download
    /// endpoint; deploy parameters (<c>AETHEUS_DEPLOY_*</c>) travel in environment variables. The
    /// binary mode performs an atomic symlink flip + a root-owned restart helper through an argv-exact
    /// sudoers grant (no unit generation); the container mode runs <c>docker load</c> +
    /// <c>docker compose up -d --wait</c>. Requires the deployment module's capability. Handled by
    /// <c>DeployOperationExecutor</c> in <c>Aetheus.Agent.Core</c>.
    /// </summary>
    PipelineDeploy = 606,

    /// <summary>
    /// Restore a pipeline artifact into the current run workspace. The artifact is resolved by the
    /// control plane from a trusted upstream run and the agent downloads it through the same
    /// run-authorized endpoint as deployments. Handled by <c>PipelineArtifactOperationExecutor</c>.
    /// </summary>
    PipelineRestoreArtifacts = 607,

    /// <summary>
    /// ADR-030: run one scanner selected from the embedded immutable scanner manifest, collect its
    /// bounded report, upload the raw report artifact, then publish the normalized envelope. The target
    /// is the scanner key only; no shell text or image reference comes from pipeline YAML.
    /// </summary>
    PipelineRunScanner = 608,

    /// <summary>
    /// ADR-030: fetch the immutable aggregate verdict after every analysis producer has published,
    /// upload machine-readable and human-readable summaries, then enforce the common run gate.
    /// </summary>
    PipelineEvaluateAnalysisGate = 609,

    /// <summary>
    /// Promote the complete optional-observability package set through an agent-owned harness.
    /// The pipeline checkout can provide candidate artifacts but cannot replace the signing or
    /// publication program that receives the encrypted credentials.
    /// </summary>
    PipelinePublishObservabilityBundle = 610,

    /// <summary>
    /// Execute a provider-agnostic AI CLI profile. The fixed target is <c>ai-run</c>; the encrypted
    /// task environment carries the binary, argv template, prompt, workspace and output bounds.
    /// </summary>
    AiRun = 611,

    /// <summary>
    /// Probe a deployed application and record findings without deciding its fate. The three
    /// deployment paths each hand-rolled this: HTTP readiness probes, a frontend contract check and
    /// an authenticated browser suite. Failing evidence is reported through the step's output
    /// variables so a gate can grade it; the step itself only fails on a technical fault that makes
    /// the evidence meaningless. Target is the probed origin; bounds and the optional browser image
    /// travel in <c>AETHEUS_SMOKE_*</c> env vars. Handled by <c>SmokeOperationExecutor</c>.
    /// </summary>
    PipelineSmoke = 612,

    // Blue-green host deployment. The three deployment paths each re-implemented this sequence in
    // shell; these four operations are that sequence, factored so any project can reuse it. They
    // share one on-disk journal under the environment's state directory, which is what lets them run
    // as separate pipeline steps: each reads the state the previous one committed rather than
    // depending on a lock held inside one long-lived process.
    /// <summary>
    /// Bring up the shared database, then reconcile schema: read the applied EF history, detect
    /// pending migrations, enforce the expand/contract contract against them and run the migration
    /// bundle once as a dedicated job. Target is the Compose project. Handled by
    /// <c>BlueGreenOperationExecutor</c>.
    /// </summary>
    BlueGreenMigrate = 613,

    /// <summary>
    /// Select the idle colour from the persisted live colour, start it, and hold until it reports
    /// ready on its own ports. Never touches the live colour, so a failure here leaves the current
    /// deployment serving. Target is the Compose project.
    /// </summary>
    BlueGreenUp = 614,

    /// <summary>
    /// Open the transaction journal, point the web server at the idle colour and reload it. The
    /// reload is self-validating: a rejected configuration keeps the previous colour serving, and the
    /// journal lets a later step reconcile an interrupted cutover. Target is the Compose project.
    /// </summary>
    BlueGreenSwitch = 615,

    /// <summary>
    /// Close the transaction: record the deployed revision, stop the colour that was replaced and
    /// clear the journal. Runs only after the evidence steps have had their say, so a finding can
    /// never undo a cutover that is already serving. Target is the Compose project.
    /// </summary>
    BlueGreenCommit = 616,

    /// <summary>
    /// Put traffic back on the colour that was serving before the switch, then stop the candidate.
    /// The evidence steps run after the cutover, so without this a failing probe would leave traffic
    /// on a bad colour with the transaction half-open. Runs as a failure-condition step. Target is
    /// the Compose project.
    /// </summary>
    BlueGreenRollback = 617,

    /// <summary>
    /// Undo a FIRST deployment that already moved traffic. There is no previous colour to restore, so
    /// this is not a rollback and BlueGreenRollback rightly refuses it rather than taking the site
    /// down under that name - but it then left the environment wedged, because the journal keeps
    /// refusing every later deployment until someone resolves it, and nothing resolved it. This is
    /// that missing operation: restore the recorded upstream, stop the switched colour and close the
    /// transaction. It refuses when a previous colour exists, because that case IS a rollback. The
    /// shared database and the environment file are never touched; secret-zero is not regenerable.
    /// Target is the Compose project.
    /// </summary>
    BlueGreenRetire = 618,

    /// <summary>Restore the recorded reserve upstream configuration without restart or migration
    /// and make the departed colour the reserve. Refuse absent reserves, open transactions or
    /// failed readiness; target is the Compose project.</summary>
    BlueGreenRevert = 619,

    // App backups (ADR-024 4.3) - dump a managed app's DB + archive its files into a retained artifact,
    // and verify recoverability with a restore-check on a throwaway target. argv-only; DB creds travel in
    // an encrypted env var and are piped/passed off the process list, never interpolated into a shell.
    /// <summary>Run a backup: dump the DB (pg_dump/mysqldump) + archive declared file paths, then upload
    /// the archive to the backend artifact store (retained). The dump/paths/engine/creds travel in
    /// <c>AETHEUS_BACKUP_*</c> env vars (creds encrypted). Target is the policy id. Handled by
    /// <c>BackupOperationExecutor</c>.</summary>
    BackupExecute = 620,

    /// <summary>Restore-check (the no-fake core): download a backup archive, restore it onto a REAL
    /// throwaway target (a dedicated scratch DB created for the test and dropped after), verify, and
    /// report pass/fail. A backup is "verified" only after this succeeds; a failure is red, never masked.
    /// Target is the backup-run id; the archive ref + engine travel in <c>AETHEUS_BACKUP_*</c> env vars.</summary>
    BackupRestoreCheck = 621,

    /// <summary>Explicit destructive restore of a previously verified backup onto its configured live
    /// database. It is only dispatched by a manual rollback pipeline after the control plane validates
    /// the backup/project/server relationship; never by the scheduler.</summary>
    BackupRestore = 622,

    /// <summary>Execute the target .NET test project and classify TRX evidence as clean, findings
    /// or technical failure through PipelineDotnetTestOperationExecutor. Additional inputs use
    /// AETHEUS_DOTNET_TEST_* environment variables.</summary>
    PipelineDotnetTest = 623,

    /// <summary>Aggregate gate statuses with missing evidence treated as failure through
    /// PipelineGateStatusOperationExecutor. AETHEUS_GATE_* supplies statuses and policy; target is
    /// unused.</summary>
    PipelineGateStatus = 624,

    /// <summary>Publish reliability.mutation.score through PipelineMutationPublisher from exactly
    /// one mutation report selected by the JSON glob array in Command.</summary>
    PipelinePublishMutation = 625,

    /// <summary>Run a build-level function of PipelineFunctionCatalog (a <c>type: aetheus</c> step).
    /// Target is the function name, which must be a Build entry of the catalog; the <c>with:</c>
    /// values travel in AETHEUS_FUNCTION_PARAMETERS_JSON.</summary>
    PipelineFunction = 626,

    /// <summary>Run a deployment-level function of PipelineFunctionCatalog. Same contract as
    /// <see cref="PipelineFunction"/>, but the target must be a Deployment entry and the task sits
    /// behind the deployment.functions grant.</summary>
    PipelineDeploymentFunction = 627,

    // Cron (Phase 3) - typed save/delete via the aetheus-cron-apply sudo helper. The helper is the
    // security boundary: root-owned, agent-non-writable, re-validates every field, and can ONLY write
    // /etc/cron.d/aetheus-<id> (fixed dir + prefix). Replaces the old free-form shell tasks that
    // CommandValidator rejected for their pipes/subshells.
    /// <summary>
    /// Write (create/update) a cron job. <c>Command</c> carries the job id (validated as
    /// <c>CronValidation.IsValidIdentifier</c>); user/schedule/command travel in the
    /// <c>AETHEUS_CRON_USER</c> / <c>AETHEUS_CRON_SCHEDULE</c> / <c>AETHEUS_CRON_COMMAND</c>
    /// environment variables (re-validated agent-side, then passed argv-only to the helper).
    /// </summary>
    CronSave = 750,

    /// <summary>Delete a cron job. <c>Command</c> carries the job id; the helper removes
    /// <c>/etc/cron.d/aetheus-&lt;id&gt;</c>.</summary>
    CronDelete = 751,

    // TeamSpeak (S-TECH-87) - ServerQuery write/query path over native TCP instead of `nc` shell-out.
    /// <summary>
    /// S-TECH-87: run a single TeamSpeak ServerQuery command (kick/ban/move/poke, channel/server
    /// edit, snapshot, groups, tokens, complaints, …) over a native <c>TcpClient</c> instead of the
    /// old <c>printf … | nc</c> shell task. <c>Command</c> carries the inner ServerQuery line
    /// (already <c>EscapeServerQuery</c>-encoded server-side); the query port is in the
    /// <c>TEAMSPEAK_QUERY_PORT</c> environment variable. The agent reads the serveradmin credential
    /// from its local credentials file and prepends <c>login … / use sid=1</c> - no credential ever
    /// travels in the task. Handled by <c>TeamspeakServerQueryOperationExecutor</c>.
    /// </summary>
    TeamspeakServerQuery = 700,

    /// <summary>
    /// Full TeamSpeak 3 server install via the root-owned <c>aetheus/teamspeak-setup</c> helper
    /// through an argv-exact sudoers grant - the same controlled-sudo recipe as mail-setup. Replaces
    /// the old free-form shell pipeline (useradd / cat &gt; /etc/systemd / systemctl enable) that always
    /// failed for the non-root agent. Target is the install path (validated against
    /// <see cref="Aetheus.Shared.Components.Shared.OperationTargetValidator"/>); the voice/query ports travel
    /// in <c>AETHEUS_TEAMSPEAK_*</c> env vars (re-validated agent-side). No secret involved - the
    /// helper extracts the generated serveradmin credential into the agent-readable credentials file.
    /// Requires the server-management module's teamspeak-setup capability.
    /// </summary>
    TeamspeakSetup = 701,

    /// <summary>Graceful TeamSpeak restart: broadcast a warning (gm), wait, kick everyone, then restart
    /// the <c>ts3server</c> systemd unit. Composite agent-side sequence over the native ServerQuery TCP
    /// client (gm/kick) plus <c>sudo -n systemctl restart ts3server</c> (service-control capability).
    /// Target is unused; the warning seconds/message and query port travel in <c>TEAMSPEAK_*</c> env vars.
    /// Replaces the dead <c>printf|nc &amp;&amp; sleep &amp;&amp; ... &amp;&amp; systemctl restart</c> shell chain the
    /// CommandValidator rejected. Handled by <c>TeamspeakGracefulRestartOperationExecutor</c>.</summary>
    TeamspeakGracefulRestart = 702,

    /// <summary>Typed read of the TeamSpeak server logs. Target is the install path; the agent tails the
    /// newest file under <c>{installPath}/logs/</c> directly (read access via the teamspeak group) -
    /// replaces the dead <c>tail ... | sort | tail</c> shell builder. Handled by
    /// <c>TeamspeakGracefulRestartOperationExecutor</c>.</summary>
    TeamspeakGetLogs = 703
}
