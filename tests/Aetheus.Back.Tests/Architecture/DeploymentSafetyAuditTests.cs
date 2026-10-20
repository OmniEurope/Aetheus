// SPDX-License-Identifier: EUPL-1.2

using Aetheus.Back.Services;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Delivery-safety guards (STD-TESTSCOPE exception). The consistency rules that used to live here
/// (blue-green healthchecks, image permissions, SDK floor policy, data-protection volume, ACME and
/// runtime cache policy of the Apache templates) are checked before every push by
/// scripts/check-delivery-config.ps1 (PLAN-002 lot 5, AUD-08).
/// </summary>
public sealed class DeploymentSafetyAuditTests
{
    private static string Root => FindRepoRoot();
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    /// <summary>Host configuration rendered by the installer from versioned templates with named
    /// substitution markers.</summary>
    private static string HostConfig(string relative) => Read("deploy", "agent-host-config", relative);

    [Fact]
    public void BlueGreenPortsAreLoopbackOnly_AndMigrationsAreSingleJob()
    {
        var compose = Read("deploy", "compose", "remote-bluegreen.compose.yml");
        foreach (var port in new[] { "PORT_BACK_BLUE", "PORT_BACK_GREEN", "PORT_FRONT_BLUE", "PORT_FRONT_GREEN" })
            Assert.Contains($"127.0.0.1:${{{port}", compose, StringComparison.Ordinal);
        Assert.Contains("Database__SkipMigrations=true", compose, StringComparison.Ordinal);
        Assert.Contains("AETHEUS_RUN_MIGRATIONS=false", compose, StringComparison.Ordinal);

        // Migration-job and no-build contracts belong to typed blue-green executors, where the
        // operations actually run.
        var executor = Read("src", "Aetheus.Agent.Core", "Operations", "BlueGreenOperationExecutor.cs");
        Assert.Contains("\"AETHEUS_MIGRATE_ONLY=true\"", executor, StringComparison.Ordinal);
        Assert.Contains("\"up\", \"-d\", \"--wait\", \"--no-build\"", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("\"build\"", executor, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cold `dotnet publish` compiles on its own, so three of them compile the shared projects three
    /// times. The host publish builds each application once and publishes it without rebuilding.
    /// </summary>
    [Fact]
    public void HostPublish_BuildsOnceAndPublishesWithoutRebuilding()
    {
        var publish = Read("deploy", "scripts", "publish-application.sh");
        // Each publish invocation runs to the [ -s check that follows it. The backend and the static
        // server publish --no-build; the frontend must not: a --no-build Blazor publish keeps the
        // "#[.{fingerprint}]" placeholders in index.html (run 2247, "Blazor is not defined").
        var invocations = publish.Split("\"$DOTNET\" publish").Skip(1).Select(rest => rest.Split("[ -s \"$OUTPUT")[0]).ToList();
        Assert.Contains("--no-build", invocations[0], StringComparison.Ordinal);
        Assert.DoesNotContain("--no-build", invocations[1], StringComparison.Ordinal);
        Assert.Contains("Aetheus.Front.csproj", invocations[1], StringComparison.Ordinal);
        Assert.Contains("--no-build", invocations[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// A fast deploy that flipped Apache and recorded its release but lost the step that closes its
    /// transaction (run 2240) must not lock every later fast deploy behind a directory nobody can
    /// remove without a shell on the host. A committed transaction whose candidate colour is the live
    /// colour is closed and the deploy goes on; any other leftover state is still refused.
    /// </summary>
    [Fact]
    public void FastRelease_ClosesACommittedButUnclosedTransactionInsteadOfRefusingForever()
    {
        var deploy = Read("deploy", "scripts", "release-fast-deploy.sh");

        var guard = deploy.IndexOf("if [ -e \"$TRANSACTION_DIR\" ]; then", StringComparison.Ordinal);
        var create = deploy.IndexOf("TRANSACTION_TMP=\"$TRANSACTION_DIR.tmp.$$\"", StringComparison.Ordinal);
        Assert.True(guard > 0 && create > guard);
        var guardBlock = deploy[guard..create];

        Assert.Contains("HOST_COMMITTED_PENDING_RELEASE", guardBlock, StringComparison.Ordinal);
        Assert.Contains("[ \"$LEFTOVER_IDLE\" = \"$LIVE\" ]", guardBlock, StringComparison.Ordinal);
        Assert.Contains("rm -rf \"$TRANSACTION_DIR\"", guardBlock, StringComparison.Ordinal);
        // The refusal is not gone: it is what every other leftover state still hits.
        Assert.Contains("FATAL: an unfinished fast deployment transaction already exists", guardBlock, StringComparison.Ordinal);
        Assert.Contains("exit 1", guardBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// The host goes back by itself after FAST_CONFIRM_MINUTES if the new backend is too broken to
    /// dispatch the rollback of an unconfirmed fast release (user request 2026-09-25). The approval
    /// window itself lives in the pipeline definition, which no test reads (user decision 2026-10-13).
    /// </summary>
    [Fact]
    public void FastRelease_ConfirmWatchdog_IsArmedAfterTheHostCommit_AndActsOnlyOnItsOwnTransaction()
    {
        // Armed only once the host is committed-pending, detached with no inherited output (the agent
        // waits for the step's output to close), and acting only on its own pending transaction.
        var deploy = Read("deploy", "scripts", "release-fast-deploy.sh").ReplaceLineEndings("\n");
        var committed = deploy.IndexOf("DEPLOY_COMMITTED=yes", StringComparison.Ordinal);
        var armed = deploy.IndexOf("setsid sh \"$WATCH_DIR/deploy/scripts/fast-release-confirm-watchdog.sh\"", StringComparison.Ordinal);
        Assert.True(committed > 0 && armed > committed, "the countdown must be armed after the host commit");
        Assert.Contains("</dev/null >>\"$WATCH_LOG\" 2>&1 &", deploy, StringComparison.Ordinal);
        var watchdog = Read("deploy", "scripts", "fast-release-confirm-watchdog.sh");
        Assert.Contains("[ \"$STATE\" = HOST_COMMITTED_PENDING_RELEASE ] && [ \"$CANDIDATE\" = \"$BUILD_SOURCEVERSION\" ]",
            watchdog, StringComparison.Ordinal);
    }

    /// <summary>
    /// Run 2247 went live with an index.html whose asset references were never resolved (the SDK's
    /// "#[.{fingerprint}]" placeholders), and every smoke test passed: the page had <html and a
    /// versioned component-library script. The deploy has to refuse an index that still carries a placeholder, and
    /// has to fetch the Blazor bootstrap script the index names rather than trust the name.
    /// </summary>
    [Fact]
    public void FastRelease_RefusesAnIndexWhoseAssetsWereNeverResolved()
    {
        var deploy = Read("deploy", "scripts", "release-fast-deploy.sh");

        var placeholderCheck = deploy.IndexOf("grep -q '#\\['", StringComparison.Ordinal);
        var bootstrapFetch = deploy.IndexOf("curl -fsSI \"${FRONT_URL}${BLAZOR_BOOT_PATH}\"", StringComparison.Ordinal);
        var goLive = deploy.IndexOf("TRANSACTION_TMP=\"$TRANSACTION_DIR.tmp.$$\"", StringComparison.Ordinal);
        Assert.True(placeholderCheck > 0, "the deploy never checks for unresolved asset placeholders");
        Assert.True(bootstrapFetch > placeholderCheck, "the deploy never fetches the Blazor bootstrap script it names");
        Assert.True(goLive > bootstrapFetch, "both checks must happen before the transaction that goes live");
    }

    [Fact]
    public void AgentReinstall_PreservesElevationPosture_AndNeverSilentlyRevokesCertbot()
    {
        var installer = Read("deploy", "scripts", "install-agent-linux.sh");
        var behavioralProof = Read("deploy", "scripts", "verify-agent-posture-idempotence.sh");

        // apply_sudoers revokes (rm -f) every capability whose flag is 0, so the default install
        // mode MUST derive the existing posture, and it must do so before the cleanup deletes the
        // sudoers files that are the only on-disk witnesses of that posture.
        var cleanup = installer.IndexOf("# --- Clean any previous installation ---", StringComparison.Ordinal);
        Assert.True(cleanup > 0);
        var installDerivation = installer.LastIndexOf("derive_existing_posture", cleanup, StringComparison.Ordinal);
        Assert.True(installDerivation >= 0, "The default install mode must derive the existing posture.");
        Assert.True(installDerivation < cleanup);

        // certbot-manage was the one capability missing from the derivation, so every reinstall
        // silently revoked HTTPS issuance and the production deployment failed on certbot.
        var derivation = installer.IndexOf("derive_existing_posture() {", StringComparison.Ordinal);
        var derivationEnd = installer.IndexOf("write_sudoers() {", derivation, StringComparison.Ordinal);
        Assert.True(derivation > 0 && derivationEnd > derivation);
        var derivationBody = installer[derivation..derivationEnd];
        foreach (var witness in new[]
                 {
                     "SUDOERS_FILE", "APACHE_MANAGE_SUDOERS_FILE", "CERTBOT_MANAGE_SUDOERS_FILE",
                     "RKHUNTER_MANAGE_SUDOERS_FILE", "CRON_MANAGE_SUDOERS_FILE",
                     "PORTSENTRY_MANAGE_SUDOERS_FILE", "SERVICE_ENABLE_SUDOERS_FILE",
                     "PACKAGE_MANAGE_SUDOERS_FILE", "PATCH_MANAGE_SUDOERS_FILE",
                     "FIREWALL_MANAGE_SUDOERS_FILE", "MAIL_MANAGE_SUDOERS_FILE",
                     "TEAMSPEAK_SETUP_SUDOERS_FILE", "DEPLOY_MANAGE_SUDOERS_FILE",
                 })
            Assert.Contains(witness, derivationBody, StringComparison.Ordinal);

        // An explicit --enable-*/--disable-* still wins for the capability it names, otherwise a
        // revocation could never be applied; the others keep the host's posture (audit DEP-021).
        Assert.Contains("preserve ", derivationBody, StringComparison.Ordinal);
        Assert.Contains("preserve() {\n    is_explicit \"$1\" && return 0", installer.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);

        // The agent publishes its ServerUrl as the package base URL and NuGet refuses plain HTTP.
        Assert.Contains("NuGet requires HTTPS sources", installer, StringComparison.Ordinal);
        Assert.Contains("SERVER_URL_HTTPS=", installer, StringComparison.Ordinal);

        Assert.Contains("derive_existing_posture", behavioralProof, StringComparison.Ordinal);
        Assert.Contains("Reinstall would have revoked", behavioralProof, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionSecretZero_SurvivesAgentReinstall_AndMissingStateFailsClosed()
    {
        var installer = Read("deploy", "scripts", "install-agent-linux.sh");
        var simulator = Read("deploy", "compose", "vpssim.compose.yml");
        var behavioralProof = Read(
            "deploy", "scripts", "verify-production-env-preservation.sh");

        Assert.Contains("WORK_DIR=\"/var/lib/aetheus-agent\"", installer, StringComparison.Ordinal);
        // The default is still /var/lib/aetheus-production, and the derived paths still hang off it;
        // the value is now overridable at install time (--production-state-dir) so a host that names
        // it differently does not need an edited installer. What the guard pins is the default and
        // the derivation, not the impossibility of an override.
        Assert.Contains(
            "PRODUCTION_STATE_DIR=\"${AETHEUS_PRODUCTION_STATE_DIR:-/var/lib/aetheus-production}\"",
            installer, StringComparison.Ordinal);
        Assert.Contains("--production-state-dir)", installer, StringComparison.Ordinal);
        Assert.Contains("require_state_dir", installer, StringComparison.Ordinal);
        Assert.Contains("PRODUCTION_ENV_FILE=\"$PRODUCTION_STATE_DIR/.env-prod\"", installer, StringComparison.Ordinal);
        Assert.Contains("LEGACY_PRODUCTION_ENV_FILE=\"$WORK_DIR/aetheus-prod/.env-prod\"", installer, StringComparison.Ordinal);
        Assert.Contains("preserve_production_environment", installer, StringComparison.Ordinal);
        Assert.Contains(".env-prod-$INSTALLATION_DATE", installer, StringComparison.Ordinal);
        Assert.Contains("date -u +\"%Y%m%dT%H%M%SZ\"", installer, StringComparison.Ordinal);
        Assert.Contains("canonical and legacy .env-prod files differ", installer, StringComparison.Ordinal);
        var installPreservation = installer.LastIndexOf(
            "preserve_production_environment",
            installer.IndexOf("# --- Clean any previous installation ---", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.True(installPreservation >= 0);
        // The reinstall purges the agent's own state (audit DEP-020: the deployment tree stays).
        var purge = installer.IndexOf("\npurge_agent_work_dir\n", StringComparison.Ordinal);
        Assert.True(purge > 0, "the reinstall purges the agent state");
        Assert.True(installPreservation < purge);

        // Secret-zero moved into the preparation script when the cutover became typed, and every rule
        // about it is asserted against the script that now applies them.
        var prepare = Read("deploy", "scripts", "prod-deploy-prepare.sh");
        Assert.Contains("Restoring production secrets from installation backup", prepare, StringComparison.Ordinal);
        Assert.Contains("docker volume inspect \"${COMPOSE_PROJECT:?COMPOSE_PROJECT is required}-$RESOURCE\"", prepare, StringComparison.Ordinal);
        Assert.Contains("Persistent production state exists but", prepare, StringComparison.Ordinal);
        Assert.Contains("Secret regeneration is forbidden", prepare, StringComparison.Ordinal);
        Assert.Contains("REQUIRED_SECRET in APPNAME ENV DB_USER DB_PASSWORD JWT_KEY ADMIN_PASSWORD ENCRYPTION_KEY ENCRYPTION_SALT", prepare, StringComparison.Ordinal);
        Assert.Contains("Created initial production-secret backup", prepare, StringComparison.Ordinal);
        // Backup names derive from the copied environment file so dated copies form one series.
        Assert.Contains("ENV_BACKUP_PREFIX=\"$(basename \"$ENV_FILE\")\"", prepare, StringComparison.Ordinal);
        Assert.Contains("ENV_BACKUP=\"$STATE_DIR/$ENV_BACKUP_PREFIX-$(date -u +\"%Y%m%dT%H%M%SZ\")\"", prepare, StringComparison.Ordinal);
        Assert.Contains("canonical and legacy production environments differ", prepare, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            prepare.IndexOf("PRODUCTION_STATE_PRESENT=true", StringComparison.Ordinal)
            // The first-deploy secrets are generated by the template renderer this call runs.
            < prepare.IndexOf("sh \"$WORKSPACE/deploy/scripts/render-env-sample.sh\"", StringComparison.Ordinal));

        Assert.Contains("vpssim-production-state:/var/lib/aetheus-production", simulator, StringComparison.Ordinal);
        Assert.Contains("vpssim-production-state:", simulator, StringComparison.Ordinal);
        Assert.Contains(
            "sed -n '/^preserve_production_environment() {$/,/^}$/p'",
            behavioralProof,
            StringComparison.Ordinal);
        Assert.Contains("rm -rf \"${WORK_DIR:?}\"/*", behavioralProof, StringComparison.Ordinal);
        Assert.Contains("PASS: .env-prod", behavioralProof, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentInstaller_InstallsVersionedAutonomousFullUpgradeSupervisor()
    {
        var installer = Read("deploy", "scripts", "install-agent-linux.sh");
        // The supervisor worker and its two units are templates the installer renders.
        var worker = HostConfig("agent-update/agent-posture-upgrade");
        var upgradeUnits = HostConfig("agent-update/aetheus-agent-upgrade.service")
            + HostConfig("agent-update/aetheus-agent-upgrade.path");
        var executor = Read(
            "src", "Aetheus.Agent.Core", "Operations", "AgentSelfUpdateOperationExecutor.cs");

        Assert.Contains(
            "render_host_config agent-update/agent-posture-upgrade \"$AGENT_UPDATE_WORKER_PATH.tmp.$$\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "render_host_config agent-update/aetheus-agent-upgrade.service \"$AGENT_UPDATE_SERVICE_PATH.tmp.$$\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "render_host_config agent-update/aetheus-agent-upgrade.path \"$AGENT_UPDATE_PATH_PATH.tmp.$$\"",
            installer,
            StringComparison.Ordinal);
        // Posture 3: the supervisor takes the sixth request field (capabilities to enable, R3-58).
        Assert.Contains("AGENT_POSTURE_VERSION=\"3\"", installer, StringComparison.Ordinal);
        Assert.Contains(
            "AGENT_POSTURE_VERSION_FILE=\"/etc/aetheus-agent-posture-version\"",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "PathExists=#{AGENT_UPDATE_REQUEST_FILE}#",
            upgradeUnits,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExecStart=#{AGENT_UPDATE_WORKER_PATH}#",
            upgradeUnits,
            StringComparison.Ordinal);
        Assert.Contains(
            "$server_url/downloads/releases/$target_version/$archive_name",
            worker,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$server_url/downloads/aetheus-agent-linux-x64.tar.gz",
            installer + worker,
            StringComparison.Ordinal);
        Assert.Contains("mv -- \"$request_file\" \"$running_file\"", worker, StringComparison.Ordinal);
        Assert.Contains(".agent-posture-upgrade-failed-request", worker, StringComparison.Ordinal);
        Assert.Contains(".agent-posture-upgrade-result", worker, StringComparison.Ordinal);
        Assert.Contains("stat -c %s \"$archive\"", worker, StringComparison.Ordinal);
        Assert.Contains("sha256sum \"$archive\"", worker, StringComparison.Ordinal);
        Assert.Contains("systemctl is-active --quiet aetheus-agent", worker, StringComparison.Ordinal);
        Assert.True(
            worker.IndexOf("systemctl is-active --quiet aetheus-agent", StringComparison.Ordinal)
            < worker.IndexOf("outcome=\"success\"", StringComparison.Ordinal));
        Assert.Contains(
            "sh \"$payload/install-agent-linux.sh\" --upgrade --yes",
            worker,
            StringComparison.Ordinal);
        // R3-58 / ADR-051: the sixth request field is a closed whitelist mapped to one fixed installer
        // flag. It is read as a field, never interpolated into a command, and a token outside the list
        // fails the update before the host is touched.
        Assert.Contains(
            "read -r target_version archive_name archive_size target_sha target_commit enable_capabilities extra",
            worker,
            StringComparison.Ordinal);
        Assert.Contains("patch-manage) enable_patch_manage=1 ;;", worker, StringComparison.Ordinal);
        Assert.Contains("phase=\"invalid-capabilities\"; exit 20", worker, StringComparison.Ordinal);
        Assert.Contains(
            "sh \"$payload/install-agent-linux.sh\" --upgrade --yes --enable-patch-manage",
            worker,
            StringComparison.Ordinal);
        Assert.DoesNotContain("--enable-$", worker, StringComparison.Ordinal);
        var upgradeMode = installer[installer.IndexOf("# Mode: UPGRADE", StringComparison.Ordinal)..installer.IndexOf("# Mode: INSTALL", StringComparison.Ordinal)];
        Assert.DoesNotContain(
            "preserve_production_environment",
            upgradeMode,
            StringComparison.Ordinal);
        Assert.Contains(
            "cp -a \"$rollback\"/. \"$install_dir\"/",
            worker,
            StringComparison.Ordinal);
        Assert.Contains(
            "LinuxIntegrationPostureVersionPath",
            executor,
            StringComparison.Ordinal);
        Assert.Contains(
            ".agent-posture-upgrade-request",
            executor,
            StringComparison.Ordinal);
        var supervisorInstaller = installer[
            installer.IndexOf("install_agent_update_supervisor()", StringComparison.Ordinal)..];
        supervisorInstaller = supervisorInstaller[..supervisorInstaller.IndexOf("ensure_docker_group()", StringComparison.Ordinal)];
        Assert.DoesNotContain("rm -f -- \"$AGENT_UPDATE_REQUEST_FILE\"", supervisorInstaller, StringComparison.Ordinal);
        Assert.DoesNotContain("sudo -n", executor, StringComparison.Ordinal);
    }

    /// <summary>
    /// Secret-zero and the production state directory. These moved out of the pipeline and into the
    /// preparation script when the cutover became typed, but not one of the checks may move with
    /// them: regenerating a secret over persistent state produces a database nothing can open, and it
    /// cannot be undone afterwards.
    /// </summary>
    [Fact]
    public void CanonicalDeployBootstrapsProductionStateWithoutExposingExistingSecrets()
    {
        var prepare = Read("deploy", "scripts", "prod-deploy-prepare.sh");

        // Managed paths must be real direct children of /var/lib without traversal; names come from
        // configuration.
        Assert.Contains("Production state directory contains a traversal", prepare, StringComparison.Ordinal);
        Assert.Contains("Production state directory must be a direct child of /var/lib", prepare, StringComparison.Ordinal);
        Assert.Contains("Production state directory must live under /var/lib", prepare, StringComparison.Ordinal);
        // Use the agent's configured working directory.
        Assert.Contains("AGENT_WORK_DIR=\"${AETHEUS_AGENT_WORK_DIRECTORY:?", prepare, StringComparison.Ordinal);
        Assert.Contains("stat -c %u \"$AGENT_WORK_DIR\"", prepare, StringComparison.Ordinal);
        Assert.Contains("stat -c %g \"$AGENT_WORK_DIR\"", prepare, StringComparison.Ordinal);
        Assert.Contains("Refusing to remap a non-empty production state directory", prepare, StringComparison.Ordinal);
        Assert.Contains("--network none --read-only --user 0:0", prepare, StringComparison.Ordinal);
        Assert.Contains("--cap-drop ALL --cap-add CHOWN --cap-add FOWNER", prepare, StringComparison.Ordinal);
        Assert.Contains("--security-opt no-new-privileges --pids-limit 16", prepare, StringComparison.Ordinal);
        Assert.Contains("--volume \"$STATE_DIR:/state\"", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("--volume /var/lib:/", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("--privileged", prepare, StringComparison.Ordinal);
        Assert.Contains("stat -c %a \"$STATE_DIR\")\" = 700", prepare, StringComparison.Ordinal);
        // The privileged bootstrap runs from the immutable artifact, so the image has to be loaded
        // and verified first. Ordering, not mere presence: an unverified image would be root here.
        Assert.True(
            prepare.IndexOf("docker load", StringComparison.Ordinal)
            < prepare.IndexOf("STATE_BOOTSTRAP_REQUIRED=false", StringComparison.Ordinal));
        Assert.True(
            prepare.IndexOf("Image revision is invalid", StringComparison.Ordinal)
            < prepare.IndexOf("STATE_BOOTSTRAP_REQUIRED=false", StringComparison.Ordinal));
        Assert.True(
            prepare.IndexOf("STATE_BOOTSTRAP_REQUIRED=false", StringComparison.Ordinal)
            < prepare.IndexOf("PRODUCTION_STATE_PRESENT=true", StringComparison.Ordinal));
        Assert.True(
            prepare.IndexOf("PRODUCTION_STATE_PRESENT=true", StringComparison.Ordinal)
            // The first-deploy secrets are generated by the template renderer this call runs.
            < prepare.IndexOf("sh \"$WORKSPACE/deploy/scripts/render-env-sample.sh\"", StringComparison.Ordinal));
        Assert.Contains("Persistent production state exists but", prepare, StringComparison.Ordinal);
        Assert.Contains("Secret regeneration is forbidden", prepare, StringComparison.Ordinal);
        Assert.Contains("Reusing existing secrets", prepare, StringComparison.Ordinal);
        // The preparation must not deploy: a second cutover implementation is exactly the drift the
        // shared template exists to prevent.
        foreach (var cutoverOperation in new[]
                 { "docker compose", "systemctl reload", "aetheus-apache-reload", "pg_dump" })
        {
            Assert.DoesNotContain(cutoverOperation, prepare, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BlueGreenApacheUpstream_IsRenderedFromVersionedTemplate()
    {
        var renderer = Read("deploy", "scripts", "render-apache-upstream.sh");

        Assert.Contains("/.pipeline/configs/apache/aetheus-upstream.conf", renderer, StringComparison.Ordinal);
        // The destination must equal the environment's UPSTREAM_CONF.
        Assert.Contains("[ \"$output\" = \"$expected\" ]", renderer, StringComparison.Ordinal);
        Assert.Contains("UPSTREAM_CONF must name a file in a sites-available directory", renderer, StringComparison.Ordinal);
        Assert.Contains("UPSTREAM_DEFINE must be upper case, digits and underscores", renderer, StringComparison.Ordinal);
        Assert.Contains("*[!0-9]*", renderer, StringComparison.Ordinal);
        Assert.Contains("Apache upstream template contains an unresolved token", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallersFailClosed_AndWindowsDoesNotUseLocalSystem()
    {
        var windows = Read("deploy", "scripts", "install-agent-windows.ps1");
        Assert.Contains("NT SERVICE\\$ServiceName", windows, StringComparison.Ordinal);
        Assert.Contains("Agent enrollment could not be proven", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("registration could not be confirmed from logs", windows, StringComparison.Ordinal);
        Assert.Contains("DockerStorageMaintenance", windows, StringComparison.Ordinal);
        Assert.Contains("PolicyVersion = 3", windows, StringComparison.Ordinal);
        Assert.Contains("DryRun = $false", windows, StringComparison.Ordinal);
        Assert.Contains("DeploymentOnly = $false", windows, StringComparison.Ordinal);
        Assert.Contains("AllowBuildsOnDeploymentTarget = $false", windows, StringComparison.Ordinal);
        Assert.Contains("ReservedSpaceGiB = 5", windows, StringComparison.Ordinal);
        Assert.Contains("MaxCacheGiB = 15", windows, StringComparison.Ordinal);
        Assert.Contains("${serviceIdentity}:(OI)(CI)M", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("${serviceIdentity}:(OI)(CI)RX", windows, StringComparison.Ordinal);

        var linux = Read("deploy", "scripts", "install-agent-linux.sh");
        Assert.Contains("No successful enrollment outcome was observed", linux, StringComparison.Ordinal);
        Assert.Contains("rm -f \"$CERTBOT_MANAGE_SUDOERS_FILE\" \"$TEAMSPEAK_SETUP_SUDOERS_FILE\"", linux, StringComparison.Ordinal);
        Assert.Contains("gpasswd -d \"$AGENT_USER\" docker", linux, StringComparison.Ordinal);
        Assert.Contains("gpasswd -d \"$AGENT_USER\" teamspeak", linux, StringComparison.Ordinal);
        // Appsettings.json is a template the installer renders.
        var linuxSettings = HostConfig("agent/appsettings.json");
        Assert.Contains("render_host_config agent/appsettings.json \"$INSTALL_DIR/appsettings.json\"", linux, StringComparison.Ordinal);
        Assert.Contains("\"DockerStorageMaintenance\"", linuxSettings, StringComparison.Ordinal);
        Assert.Contains("\"PolicyVersion\": 3", linuxSettings, StringComparison.Ordinal);
        Assert.Contains("\"DryRun\": false", linuxSettings, StringComparison.Ordinal);
        Assert.Contains(
            "if [ \"$MODULE_DEPLOYMENT\" -eq 1 ] && [ \"$MODULE_PIPELINE_RUNNER\" -eq 0 ]; then",
            linux,
            StringComparison.Ordinal);
        Assert.Contains("DOCKER_STORAGE_DEPLOYMENT_ONLY=true", linux, StringComparison.Ordinal);
        Assert.Contains("\"DeploymentOnly\": #{DOCKER_STORAGE_DEPLOYMENT_ONLY}#", linuxSettings, StringComparison.Ordinal);
        Assert.Contains("\"AllowBuildsOnDeploymentTarget\": false", linuxSettings, StringComparison.Ordinal);
        Assert.Contains("\"ReservedSpaceGiB\": 5", linuxSettings, StringComparison.Ordinal);
        Assert.Contains("\"MaxCacheGiB\": 15", linuxSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void CertbotIssueHelper_UsesWebrootInProduction_AndSelfSignsOnlyInLocalMode()
    {
        // The helper is the certbot/aetheus-certbot-issue template the installer renders.
        var script = Read("deploy", "scripts", "install-agent-linux.sh");
        Assert.Contains(
            "render_host_config certbot/aetheus-certbot-issue \"$CERTBOT_ISSUE_HELPER_PATH\"",
            script,
            StringComparison.Ordinal);
        var helper = HostConfig("certbot/aetheus-certbot-issue");
        var productionStart = helper.IndexOf("if [ \"$mode\" = production ]; then", StringComparison.Ordinal);
        var localStart = helper.IndexOf("# Local-only certificate", productionStart + 1, StringComparison.Ordinal);
        Assert.True(productionStart >= 0 && localStart > productionStart);

        var production = helper[productionStart..localStart];
        Assert.Contains("certbot certonly --webroot --webroot-path", production, StringComparison.Ordinal);
        Assert.Contains("certbot ACME validation failed", production, StringComparison.Ordinal);
        Assert.Contains("exit 1", production, StringComparison.Ordinal);
        Assert.DoesNotContain("certonly --apache", production, StringComparison.Ordinal);
        Assert.DoesNotContain("openssl req -x509", production, StringComparison.Ordinal);
        Assert.DoesNotContain("self-signed", production, StringComparison.OrdinalIgnoreCase);

        var local = helper[localStart..];
        Assert.Contains("if [ \"$mode\" = local ]", helper, StringComparison.Ordinal);
        Assert.Contains("ACME is not contacted", helper, StringComparison.Ordinal);
        Assert.Contains("openssl req -x509", local, StringComparison.Ordinal);
        Assert.Contains("local self-signed HTTPS", local, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HistoricalDeployRequiresBackupAndReadiness()
    {
        var script = Read("deploy", "scripts", "deploy.sh");
        Assert.Contains("pg_dump FAILED - refusing to deploy", script, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:${PORT_BACK}/health/ready", script, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_dump FAILED - no pre-migration backup", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ApacheConfigAssets_UseVersionedExternalConfigsAndTransactionalTypedApply()
    {
        // The whole module, not one file: the Apache step factory has already moved once, and a
        // negative assertion pointed at the file it left would pass without checking anything.
        var pipelinesModule = Directory
            .EnumerateFiles(
                Path.Combine(Root, "src", "Aetheus.Back", "Components", "Pipelines"),
                "*.cs",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();
        Assert.True(pipelinesModule.Count > 10, "The pipelines-module scan is unexpectedly small.");
        Assert.DoesNotContain(pipelinesModule, source => source.Contains("<VirtualHost", StringComparison.Ordinal));
        Assert.DoesNotContain(pipelinesModule, source => source.Contains("ProxyPassReverse", StringComparison.Ordinal));

        var applier = Read("src", "Aetheus.Agent.Core", "Operations", "ApacheConfigSetApplier.cs");
        Assert.Contains("Apache config test failed; restoring the previous configuration set", applier, StringComparison.Ordinal);
        Assert.Contains("Apache reload failed; restoring the previous configuration set", applier, StringComparison.Ordinal);
        Assert.Contains("RestoreAndReloadAsync", applier, StringComparison.Ordinal);
    }

    [Fact]
    public void ApacheConfigTest_UsesFixedRootOwnedHelperWithoutNoexec()
    {
        // The helper and the drop-in are templates the installer renders.
        var installer = Read("deploy", "scripts", "install-agent-linux.sh");
        var sudoers = HostConfig("apache/sudoers.d/aetheus-apache");
        var blockStart = sudoers.IndexOf("Cmnd_Alias AETHEUS_APACHE_TEST", StringComparison.Ordinal);
        Assert.True(blockStart >= 0);
        var sudoersBlock = sudoers[blockStart..];

        Assert.Contains("exec /usr/sbin/apache2ctl configtest", HostConfig("apache/aetheus-apache-configtest"), StringComparison.Ordinal);
        Assert.Contains("render_host_config apache/aetheus-apache-configtest \"$APACHE_CONFIGTEST_HELPER_PATH\"", installer, StringComparison.Ordinal);
        Assert.Contains("render_host_config apache/sudoers.d/aetheus-apache \"$APACHE_MANAGE_SUDOERS_FILE\"", installer, StringComparison.Ordinal);
        Assert.Contains("chown root:root \"$APACHE_CONFIGTEST_HELPER_PATH\"", installer, StringComparison.Ordinal);
        Assert.Contains("chmod 755 \"$APACHE_CONFIGTEST_HELPER_PATH\"", installer, StringComparison.Ordinal);
        Assert.Contains("Cmnd_Alias AETHEUS_APACHE_TEST = #{APACHE_CONFIGTEST_HELPER_PATH}#", sudoersBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Defaults!AETHEUS_APACHE_TEST noexec", sudoersBlock, StringComparison.Ordinal);
        Assert.Contains("Defaults!AETHEUS_APACHE_MANAGE noexec", sudoersBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("/usr/sbin/apache2ctl configtest", sudoersBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildToolchainsAndRemoteHostTrustArePinned()
    {

        var sdkBootstrap = Read("deploy", "scripts", "ensure-dotnet-sdk.sh");
        Assert.Contains("SDK_VERSION=\"$(sed", sdkBootstrap, StringComparison.Ordinal);
        Assert.Contains("${AETHEUS_DOTNET_ROOT:-$HOME/.aetheus/dotnet}", sdkBootstrap, StringComparison.Ordinal);
        Assert.Contains("INSTALL_DIR=\"$INSTALL_ROOT/$SDK_VERSION\"", sdkBootstrap, StringComparison.Ordinal);
        Assert.Contains("resolves_exact_sdk", sdkBootstrap, StringComparison.Ordinal);
        Assert.Contains("$1\" --version", sdkBootstrap, StringComparison.Ordinal);
        Assert.Contains("--version \"$SDK_VERSION\"", sdkBootstrap, StringComparison.Ordinal);
        Assert.Contains("Pinned .NET SDK $SDK_VERSION installation could not be verified", sdkBootstrap, StringComparison.Ordinal);

        var nodeBootstrap = Read("deploy", "scripts", "ensure-node-runtime.sh");
        Assert.Contains("https://nodejs.org/dist/v${NODE_VERSION}/${NODE_ARCHIVE}", nodeBootstrap, StringComparison.Ordinal);
        Assert.Contains("sha256sum -c -", nodeBootstrap, StringComparison.Ordinal);
        Assert.Contains("$HOME/.aetheus/node-v${NODE_VERSION}-linux-x64", nodeBootstrap, StringComparison.Ordinal);
        Assert.Contains("Pinned Node.js $NODE_VERSION installation could not be verified", nodeBootstrap, StringComparison.Ordinal);

        var deploymentFiles = RepositoryScan
            .EnumerateTopLevel(Path.Combine(Root, "deploy", "pipelines"), "*.yaml")
            .Concat(RepositoryScan.EnumerateTopLevel(Path.Combine(Root, "deploy", "docker"), "Dockerfile*"));
        foreach (var path in deploymentFiles)
        {
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("StrictHostKeyChecking=no", content, StringComparison.Ordinal);
            Assert.DoesNotContain("UserKnownHostsFile=/dev/null", content, StringComparison.Ordinal);
            Assert.DoesNotContain("--channel 10.0", content, StringComparison.Ordinal);
            Assert.DoesNotContain("10.0.x", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void VpsSimulator_HasNoDefaultPassword_PinsItsDownloads_AndRefusesVitrineTraversal()
    {
        var compose = Read("deploy", "compose", "vpssim.compose.yml");
        var dockerfile = Read("deploy", "docker", "Dockerfile.vpssim");

        Assert.DoesNotContain("nodejs.org", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("node --version", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("npm --version", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("ARG NODE_VERSION", dockerfile, StringComparison.Ordinal);

        var bootstrap = Read("deploy", "docker", "vpssim-agent-bootstrap.sh");
        Assert.Contains("[ -x /opt/aetheus-agent/Aetheus.Agent.Linux ]", bootstrap, StringComparison.Ordinal);
        Assert.Contains("rm -f /var/lib/vpssim-agent-installed", bootstrap, StringComparison.Ordinal);
        Assert.Contains("^https://(host\\.docker\\.internal|localhost|127\\.0\\.0\\.1)", bootstrap, StringComparison.Ordinal);
        Assert.Contains("set AGENT_TOKEN or VPSSIM_ADMIN_PASSWORD", bootstrap, StringComparison.Ordinal);
        Assert.Contains("AGENT_TOKEN=${VPSSIM_AGENT_TOKEN:-}", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("ADMIN_PASSWORD=\"${ADMIN_PASSWORD:-aetheus-dev-admin-pwd}\"", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("VPSSIM_ADMIN_PASSWORD:-aetheus-dev-admin-pwd", compose, StringComparison.Ordinal);

        var vpsSimulator = Read("deploy", "docker", "Dockerfile.vpssim");
        Assert.Contains(
            "a2enmod rewrite headers ssl proxy proxy_http proxy_wstunnel",
            vpsSimulator,
            StringComparison.Ordinal);
        Assert.Contains("/etc/letsencrypt/options-ssl-apache.conf", vpsSimulator, StringComparison.Ordinal);
        Assert.Contains("SSLProtocol all -SSLv2 -SSLv3 -TLSv1 -TLSv1.1", vpsSimulator, StringComparison.Ordinal);
        Assert.Contains(
            "775a5731a9809801e4c8f9066cd9bc562a1b368553139c1249f2a0740d50041e  /tmp/ts3.tar.bz2\" | sha256sum -c -",
            vpsSimulator,
            StringComparison.Ordinal);

        var provisioning = Read("deploy", "docker", "vpssim-provision.sh");
        Assert.Contains("set -eu", provisioning, StringComparison.Ordinal);

        var publishVitrine = Read("deploy", "scripts", "publish-vitrine.sh");
        // The destination must be a direct child of /var/www without traversal; project naming is
        // configuration rather than a filesystem boundary.
        Assert.Contains("/var/www/*/*|*..*", publishVitrine, StringComparison.Ordinal);
        Assert.Contains("/var/www/?*", publishVitrine, StringComparison.Ordinal);
        Assert.Contains("readlink -m -- \"$WEB_ROOT\"", publishVitrine, StringComparison.Ordinal);
        Assert.Contains("Refusing publication through a symbolic-link web root", publishVitrine, StringComparison.Ordinal);
        Assert.Contains("Refusing to publish a vitrine containing unresolved", publishVitrine, StringComparison.Ordinal);
        Assert.DoesNotContain("grep -rc \"aetheus\\.example\" site/ || true", publishVitrine, StringComparison.Ordinal);
    }


    [Fact]
    public void ProductCoverageIncludesEveryHandwrittenSourceFile()
    {
        var settings = Read("tests", "testconfig.json");
        Assert.Contains("**/obj/**/*.cs", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("**/Program.cs", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoGeneratedProgram", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("DesignTimeDbContextFactory", settings, StringComparison.Ordinal);

        var logger = Read("src", "Aetheus.Back", "Middleware", "JsonFileLoggerProvider.cs");
        Assert.DoesNotContain("ExcludeFromCodeCoverage", logger, StringComparison.Ordinal);
        Assert.Contains("DroppedEntries", logger, StringComparison.Ordinal);
    }

    /// <summary>The versioned production environment template contains generation markers rather
    /// than secrets, and no value may resemble a generated secret.</summary>
    [Fact]
    public void TheProductionEnvironmentTemplate_HoldsMarkers_NeverSecrets()
    {
        var lines = Read("deploy", "env", "prod.env.sample").ReplaceLineEndings("\n").Split('\n')
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

        foreach (var secret in new[]
                 {
                     "DB_PASSWORD", "JWT_KEY", "ADMIN_PASSWORD", "ENCRYPTION_KEY", "ENCRYPTION_SALT",
                     "DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY",
                 })
        {
            Assert.Single(lines, line => line.StartsWith(secret + "=", StringComparison.Ordinal));
            Assert.Matches($"^{secret}=#\\{{SECRET_HEX_[0-9]+\\}}#$", lines.Single(line => line.StartsWith(secret + "=", StringComparison.Ordinal)));
        }

        Assert.DoesNotContain(lines, line => System.Text.RegularExpressions.Regex.IsMatch(line, "=[0-9a-fA-F]{16,}$"));
        // The proof runs the script's own first-deploy block, not a copy of it.
        var proof = Read("deploy", "scripts", "verify-prod-env-sample.sh");
        Assert.Contains("PREPARE=\"$ROOT/deploy/scripts/prod-deploy-prepare.sh\"", proof, StringComparison.Ordinal);
        Assert.Contains("\"$PREPARE\" > \"$BLOCK\"", proof, StringComparison.Ordinal);
    }

    /// <summary>The container mirror answers like production, or the demo and the local mirror prove
    /// nothing about the site the user actually loads.</summary>
    [Fact]
    public void TheContainerMirrorAppliesTheSameRuntimeCachePolicy()
    {
        var server = Read("deploy", "docker", "StaticServer.Program.cs");
        Assert.Contains("\"/_framework\"", server, StringComparison.Ordinal);
        Assert.Contains("public, max-age=31536000, immutable", server, StringComparison.Ordinal);
        // Only for a file actually served: an immutable 404 would be stuck in the browser for a year.
        Assert.Contains("context.Response.StatusCode < 400", server, StringComparison.Ordinal);
        // The unfingerprinted assets are revalidated there too, not refused storage (D17).
        Assert.Contains("CacheControl = \"no-cache\"", server, StringComparison.Ordinal);
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;
}
