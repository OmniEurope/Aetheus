// SPDX-License-Identifier: EUPL-1.2

using Aetheus.Back.Services;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Tests.Architecture;

public sealed class DeploymentSafetyAuditTests
{
    private static string Root => FindRepoRoot();
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    /// <summary>R-249: a host configuration file install-agent-linux.sh renders from
    /// deploy/agent-host-config (the former heredoc, #{NAME}# where it expanded $NAME).</summary>
    private static string HostConfig(string relative) => Read("deploy", "agent-host-config", relative);

    [Fact]
    public void BlueGreenPortsAreLoopbackOnly_AndMigrationsAreSingleJob()
    {
        var compose = Read("deploy", "compose", "remote-bluegreen.compose.yml");
        foreach (var port in new[] { "PORT_BACK_BLUE", "PORT_BACK_GREEN", "PORT_FRONT_BLUE", "PORT_FRONT_GREEN" })
            Assert.Contains($"127.0.0.1:${{{port}", compose, StringComparison.Ordinal);
        Assert.Contains("Database__SkipMigrations=true", compose, StringComparison.Ordinal);
        Assert.Contains("AETHEUS_RUN_MIGRATIONS=false", compose, StringComparison.Ordinal);

        // The single migration job and the never-build rule moved into bluegreen-migrate/-up when the
        // production cutover became typed; the pipeline no longer contains a Compose invocation at
        // all. Asserting them against the executor is what keeps them enforced rather than merely
        // absent from a file that stopped mentioning them.
        var executor = Read("src", "Aetheus.Agent.Core", "Operations", "BlueGreenOperationExecutor.cs");
        Assert.Contains("\"AETHEUS_MIGRATE_ONLY=true\"", executor, StringComparison.Ordinal);
        Assert.Contains("\"up\", \"-d\", \"--wait\", \"--no-build\"", executor, StringComparison.Ordinal);
        Assert.DoesNotContain("\"build\"", executor, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every colour of the blue-green stack must declare a healthcheck, because the deploy relies on
    /// <c>compose up --wait</c> to know when a colour is usable. A service without one is reported ready
    /// the moment its container runs, which is not the same thing: production run 2218 failed on
    /// <c>curl (7)</c> against a front that had been up for less than a second, and the front was the one
    /// service in the file with no healthcheck.
    /// </summary>
    [Fact]
    public void EveryBlueGreenService_DeclaresAHealthcheck()
    {
        var compose = Read("deploy", "compose", "remote-bluegreen.compose.yml");

        // Both colours inherit their definition from a YAML anchor, so one healthcheck per anchor is
        // what "every service" means here: back-common, front-common and the shared database.
        Assert.Equal(3, compose.Split("healthcheck:").Length - 1);

        // Naming the front probe explicitly: the anchor count alone would still pass if someone moved
        // the backend probe around, and it is the front that was missing.
        var frontAnchor = compose.IndexOf("<<: &front-common", StringComparison.Ordinal);
        var frontBlue = compose.IndexOf("container_name: ${APPNAME}-${ENV}-blue-front", StringComparison.Ordinal);
        Assert.True(frontAnchor > 0 && frontBlue > frontAnchor);
        var frontCommon = compose[frontAnchor..frontBlue];
        Assert.Contains("healthcheck:", frontCommon, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:8080/", frontCommon, StringComparison.Ordinal);

        // The probe runs curl inside the container, so the image has to carry it.
        var frontImage = Read("deploy", "docker", "Dockerfile.front");
        Assert.Contains("curl", frontImage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both production images now copy a publish written on the host, and COPY keeps the permissions
    /// the agent wrote it with. Under a 077 umask that leaves the runtimeconfig root-only, and the .NET
    /// host fails as InvalidConfigFile (exit 147) as soon as the image drops to USER app. The backend
    /// normalised /app from the start; the frontend did not, and production runs 2218 and 2229
    /// crash-looped on it. Reading the Dockerfiles is the only way to keep both honest, since the
    /// failure only shows on a host whose umask differs from the developer's.
    /// </summary>
    [Theory]
    [InlineData("Dockerfile.front")]
    [InlineData("Dockerfile.back")]
    public void ProductionImage_MakesTheHostPublishReadableBeforeDroppingRoot(string dockerfile)
    {
        var image = Read("deploy", "docker", dockerfile);

        var normalise = image.IndexOf("chmod -R a+rX /app", StringComparison.Ordinal);
        var dropRoot = image.LastIndexOf("USER app", StringComparison.Ordinal);
        Assert.True(normalise > 0, $"{dockerfile} never makes /app world-readable.");
        Assert.True(dropRoot > normalise, $"{dockerfile} drops to USER app before normalising /app.");
    }

    /// <summary>
    /// The fast release build went from 10 to 19 minutes on an unchanged Dockerfile because a bare
    /// `docker build` runs on the daemon's default builder, which nobody maintains and which shares
    /// nothing with the builder the CI just warmed for the same commit. It has to build where the CI
    /// builds, on the builder the agent prunes, and it has to say so in the same words the CI uses.
    /// </summary>
    [Fact]
    public void FastRelease_BuildsOnTheBuilderTheCiWarmsAndTheAgentMaintains()
    {
        var fast = Read("deploy", "scripts", "build-fast-release-images.sh");
        var ci = Read("deploy", "scripts", "ci", "package-application.sh");

        Assert.DoesNotContain("docker build ", fast, StringComparison.Ordinal);
        Assert.Contains("ensure-buildx-builder.sh \"$BUILDER\"", fast, StringComparison.Ordinal);
        Assert.Equal(2, fast.Split("buildx-build-load.sh --builder \"$BUILDER\" --load").Length - 1);

        // One expression for the builder name, so a rename in one script cannot silently split the cache.
        const string builderExpression = "BUILDER=\"${AETHEUS_BUILDX_BUILDER:-aetheus-$(hostname 2>/dev/null || echo agent)}\"";
        Assert.Contains(builderExpression, fast, StringComparison.Ordinal);
        Assert.Contains(builderExpression, ci, StringComparison.Ordinal);

        // The NuGet cache mount is keyed by cohort; "shared" is what the CI uses on its warm path.
        Assert.Contains("CACHE_COHORT=shared", fast, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cold `dotnet publish` compiles on its own, so three of them compile the shared projects three
    /// times. The host publish builds each application once and publishes it without rebuilding.
    /// </summary>
    [Fact]
    public void HostPublish_BuildsOnceAndPublishesWithoutRebuilding()
    {
        var publish = Read("deploy", "scripts", "publish-application.sh");
        var publishLines = publish.Split('\n').Where(line => line.Contains("\"$DOTNET\" publish", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, publishLines.Count);
        // Each publish invocation runs to the [ -s check that follows it. The backend and the static
        // server publish --no-build; the frontend must not: a --no-build Blazor publish keeps the
        // "#[.{fingerprint}]" placeholders in index.html (run 2247, "Blazor is not defined").
        var invocations = publish.Split("\"$DOTNET\" publish").Skip(1).Select(rest => rest.Split("[ -s \"$OUTPUT")[0]).ToList();
        Assert.Contains("--no-build", invocations[0], StringComparison.Ordinal);
        Assert.DoesNotContain("--no-build", invocations[1], StringComparison.Ordinal);
        Assert.Contains("Aetheus.Front.csproj", invocations[1], StringComparison.Ordinal);
        Assert.Contains("--no-build", invocations[2], StringComparison.Ordinal);
        Assert.Equal(2, publish.Split("\"$DOTNET\" build ").Length - 1);
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
        // Named after the file they copy (.env-prod-<date> in production), so the installer's dated
        // copies and these are one series (PLAN-003 2.1: no literal file name).
        Assert.Contains("ENV_BACKUP_PREFIX=\"$(basename \"$ENV_FILE\")\"", prepare, StringComparison.Ordinal);
        Assert.Contains("ENV_BACKUP=\"$STATE_DIR/$ENV_BACKUP_PREFIX-$(date -u +\"%Y%m%dT%H%M%SZ\")\"", prepare, StringComparison.Ordinal);
        Assert.Contains("canonical and legacy production environments differ", prepare, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            prepare.IndexOf("PRODUCTION_STATE_PRESENT=true", StringComparison.Ordinal)
            // R-248: the first-deploy secrets are generated by the template renderer this call runs.
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
        // R-249: the supervisor worker and its two units are templates the installer renders.
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
        Assert.Contains("AGENT_POSTURE_VERSION=\"2\"", installer, StringComparison.Ordinal);
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
        Assert.Contains("--retry 5 --retry-all-errors", worker, StringComparison.Ordinal);
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
        var upgradeMode = installer[installer.IndexOf("# Mode: UPGRADE", StringComparison.Ordinal)..installer.IndexOf("# Mode: INSTALL", StringComparison.Ordinal)];
        Assert.DoesNotContain(
            "preserve_production_environment",
            upgradeMode,
            StringComparison.Ordinal);
        Assert.Contains(
            "cp -a \"$rollback\"/. \"$install_dir\"/",
            worker,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            installer.Split("install_agent_update_supervisor \"", StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "LinuxIntegrationPostureVersionPath",
            executor,
            StringComparison.Ordinal);
        Assert.Contains(
            ".agent-posture-upgrade-request",
            executor,
            StringComparison.Ordinal);
        Assert.Contains("sleep 5", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("sleep 150", installer + worker, StringComparison.Ordinal);
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

        // The boundary the guard exists for, not one literal path: a real, direct child of /var/lib,
        // with no traversal. Files are written and removed under it, and that is what must be bounded;
        // its name belongs to the aetheus-prod-host library (PLAN-006 lot 10).
        Assert.Contains("Production state directory contains a traversal", prepare, StringComparison.Ordinal);
        Assert.Contains("Production state directory must be a direct child of /var/lib", prepare, StringComparison.Ordinal);
        Assert.Contains("Production state directory must live under /var/lib", prepare, StringComparison.Ordinal);
        // The agent's own work directory, as the agent names it (PLAN-003 2.1).
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
            // R-248: the first-deploy secrets are generated by the template renderer this call runs.
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
        // The destination is the environment's UPSTREAM_CONF and nothing else (PLAN-003 2.1: compared
        // with the variable instead of one literal path, so the demo uses the same mechanism).
        Assert.Contains("[ \"$output\" = \"$expected\" ]", renderer, StringComparison.Ordinal);
        Assert.Contains("UPSTREAM_CONF must name a file in a sites-available directory", renderer, StringComparison.Ordinal);
        Assert.Contains("UPSTREAM_DEFINE must be upper case, digits and underscores", renderer, StringComparison.Ordinal);
        Assert.Contains("*[!0-9]*", renderer, StringComparison.Ordinal);
        Assert.Contains("Apache upstream template contains an unresolved token", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionImageRetention_IsApplicationScopedAndProtectsEveryContainerImage()
    {
        var script = Read("deploy", "scripts", "retain-docker-images.sh");
        Assert.Contains("docker ps -aq --no-trunc", script, StringComparison.Ordinal);
        Assert.Contains("flock -n 9", script, StringComparison.Ordinal);
        Assert.Contains("refresh_protected_images", script, StringComparison.Ordinal);
        Assert.Contains("docker image inspect --format '{{.Id}}' \"$tag\"", script, StringComparison.Ordinal);
        Assert.Contains("refusing stale deletion", script, StringComparison.Ordinal);
        Assert.Contains("docker inspect --format '{{.Image}}'", script, StringComparison.Ordinal);
        Assert.Contains("SKIP vanished container", script, StringComparison.Ordinal);
        Assert.Contains("--filter \"id=$container\"", script, StringComparison.Ordinal);
        Assert.Contains("refusing image retention", script, StringComparison.Ordinal);
        Assert.DoesNotContain("docker inspect --format '{{.Image}}' \"$container\" 2>/dev/null || true", script, StringComparison.Ordinal);
        Assert.Contains("grep -Fxq \"$image_id\" \"$PROTECTED\"", script, StringComparison.Ordinal);
        Assert.Contains("AETHEUS_IMAGE_RETENTION_DRY_RUN", script, StringComparison.Ordinal);
        Assert.Contains("AETHEUS_IMAGE_RETENTION_DRY_RUN:-false", script, StringComparison.Ordinal);
        Assert.Contains("docker image rm \"$tag\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("docker system prune", script, StringComparison.Ordinal);
        Assert.DoesNotContain("docker image prune", script, StringComparison.Ordinal);
        Assert.DoesNotContain("docker volume", script, StringComparison.Ordinal);
        Assert.DoesNotContain("docker image rm -f", script, StringComparison.Ordinal);
    }

    [Fact]
    public void QaStoragePlateauEvidence_IsReadOnlyPersistentAndCountedOnlyAfterSuccessfulQa()
    {
        var script = Read("deploy", "scripts", "record-storage-evidence.sh");
        var staleComposePrune = Read("deploy", "scripts", "prune-stale-qa-compose.sh");
        var prune = Read("deploy", "scripts", "prune-qa-buildx-cache.sh");

        Assert.Contains("AETHEUS_AGENT_WORK_DIRECTORY", script, StringComparison.Ordinal);
        Assert.Contains("docker buildx du --builder", script, StringComparison.Ordinal);
        Assert.Contains("AETHEUS_STORAGE_MAX_CACHE_GIB", script, StringComparison.Ordinal);
        Assert.Contains("AETHEUS_STORAGE_PLATEAU_GROWTH_GIB", script, StringComparison.Ordinal);
        Assert.Contains("label=com.docker.compose.project", script, StringComparison.Ordinal);
        Assert.Contains("^aetheus-qa-(rollback-)?[0-9]+$", script, StringComparison.Ordinal);
        Assert.Contains("Exact residual QA Compose resources detected", script, StringComparison.Ordinal);
        Assert.Contains("$SCOPE-v2.tsv", script, StringComparison.Ordinal);
        Assert.DoesNotContain("name=aetheus-qa-", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("Exact residual QA Compose resources detected", StringComparison.Ordinal)
            < script.IndexOf("mkdir -p \"$STATE_DIRECTORY\"", StringComparison.Ordinal));
        Assert.DoesNotContain("docker buildx prune", script, StringComparison.Ordinal);
        Assert.DoesNotContain("docker system prune", script, StringComparison.Ordinal);
        Assert.DoesNotContain("docker volume rm", script, StringComparison.Ordinal);
        Assert.Contains("^aetheus-qa-(rollback-)?[0-9]+$", staleComposePrune, StringComparison.Ordinal);
        Assert.Contains("PROJECT_RUN_ID\" -ge \"$CURRENT_RUN_ID", staleComposePrune, StringComparison.Ordinal);
        Assert.Contains("label=com.docker.compose.project=$PROJECT", staleComposePrune, StringComparison.Ordinal);
        Assert.Contains("docker rm -f \"$ID\"", staleComposePrune, StringComparison.Ordinal);
        Assert.Contains("docker network rm \"$ID\"", staleComposePrune, StringComparison.Ordinal);
        Assert.Contains("docker volume rm \"$ID\"", staleComposePrune, StringComparison.Ordinal);
        Assert.DoesNotContain("docker system prune", staleComposePrune, StringComparison.Ordinal);
        Assert.DoesNotContain("docker volume prune", staleComposePrune, StringComparison.Ordinal);
        Assert.Contains("docker buildx prune --builder \"$BUILDER\"", prune, StringComparison.Ordinal);
        Assert.Contains("--max-used-space", prune, StringComparison.Ordinal);
        Assert.Contains("AETHEUS_QA_BUILDX_MAX_CACHE_GIB:-32", prune, StringComparison.Ordinal);
        Assert.DoesNotContain("docker system prune", prune, StringComparison.Ordinal);
        Assert.DoesNotContain("docker image prune", prune, StringComparison.Ordinal);
        Assert.DoesNotContain("docker volume", prune, StringComparison.Ordinal);
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
        // R-249: appsettings.json is a template the installer renders.
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
        // R-249: the helper is the certbot/aetheus-certbot-issue template the installer renders.
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
        // R-249: the helper and the drop-in are templates the installer renders.
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
    public void CleanCloneBuildsProvideRequiredDatabaseSettingsBeforeStartup()
    {
        var testFactory = Read(
            "tests",
            "Aetheus.Back.Tests",
            "Shared",
            "CustomWebApplicationFactory.cs");
        Assert.Contains("builder.UseEnvironment(\"Development\")", testFactory, StringComparison.Ordinal);
        Assert.Contains("builder.UseSetting(\n            \"ConnectionStrings:Default\"", testFactory.Replace("\r\n", "\n"), StringComparison.Ordinal);

        var dockerfile = Read("deploy", "docker", "Dockerfile.back");
        var migrationBundle = dockerfile.IndexOf("dotnet-ef migrations bundle", StringComparison.Ordinal);
        Assert.True(migrationBundle >= 0, "The backend image must build an EF migration bundle.");
        var designConnection = dockerfile.IndexOf("--design-connection", migrationBundle, StringComparison.Ordinal);
        Assert.True(designConnection > migrationBundle, "The EF bundle command must pass its design-time connection explicitly.");
        Assert.DoesNotContain("ENV AETHEUS_DESIGN_CONNECTION=", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildToolchainsAndRemoteHostTrustArePinned()
    {
        // STD-SDKPIN (ADR-048): global.json is a floor any newer feature band builds from; the delivery
        // stays reproducible because ensure-dotnet-sdk.sh and every SDK image install that exact floor.
        var globalJson = Read("global.json");
        using var globalJsonDocument = System.Text.Json.JsonDocument.Parse(globalJson);
        var floor = globalJsonDocument.RootElement.GetProperty("sdk").GetProperty("version").GetString();
        Assert.False(string.IsNullOrEmpty(floor), "global.json declares no SDK floor.");
        Assert.Contains("\"rollForward\": \"latestFeature\"", globalJson, StringComparison.Ordinal);
        Assert.Contains("\"allowPrerelease\": false", globalJson, StringComparison.Ordinal);

        var linuxInstaller = Read("deploy", "scripts", "install-agent-linux.sh");
        Assert.DoesNotContain("DOTNET_SDK_VERSION=", linuxInstaller, StringComparison.Ordinal);
        Assert.DoesNotContain("ensure_dotnet_sdk", linuxInstaller, StringComparison.Ordinal);
        Assert.DoesNotContain("--channel \"$DOTNET_CHANNEL\" --install-dir /usr/share/dotnet", linuxInstaller, StringComparison.Ordinal);
        Assert.DoesNotContain("NODE_VERSION=", linuxInstaller, StringComparison.Ordinal);
        Assert.DoesNotContain("ensure_node_runtime", linuxInstaller, StringComparison.Ordinal);

        var toolchainManifest = Read(
            "deploy", "pipelines", "toto-conformance-fixture", "common",
            ".aetheus", "toolchains.lock.yaml.template");
        Assert.Contains($"version: \"{floor}\"", toolchainManifest, StringComparison.Ordinal);
        Assert.Contains(
            $"mcr.microsoft.com/dotnet/sdk:{floor}@sha256:",
            toolchainManifest,
            StringComparison.Ordinal);

        var sdkBootstrap = Read("deploy", "scripts", "ensure-dotnet-sdk.sh");
        Assert.Contains("SDK_VERSION=\"$(sed", sdkBootstrap, StringComparison.Ordinal);
        Assert.Contains("${AETHEUS_DOTNET_ROOT:-$HOME/.aetheus/dotnet}", sdkBootstrap, StringComparison.Ordinal);
        Assert.Contains("INSTALL_DIR=\"$INSTALL_ROOT/$SDK_VERSION\"", sdkBootstrap, StringComparison.Ordinal);
        Assert.Contains("resolves_exact_sdk", sdkBootstrap, StringComparison.Ordinal);
        Assert.Contains("$1\" --version", sdkBootstrap, StringComparison.Ordinal);
        Assert.Contains("--version \"$SDK_VERSION\"", sdkBootstrap, StringComparison.Ordinal);
        Assert.Contains("Pinned .NET SDK $SDK_VERSION installation could not be verified", sdkBootstrap, StringComparison.Ordinal);

        var nodeBootstrap = Read("deploy", "scripts", "ensure-node-runtime.sh");
        Assert.Contains("NODE_VERSION=\"24.4.1\"", nodeBootstrap, StringComparison.Ordinal);
        Assert.Contains(
            "NODE_LINUX_X64_SHA256=\"063f2eb299ba60e3fc9b424d8e87d0e2f6be84b39bdeadc421ee2865914c498b\"",
            nodeBootstrap,
            StringComparison.Ordinal);
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
    public void OptionalObservabilityVerification_UsesTheRepositoryNuGetConfigWithExactCase()
    {
        var script = Read("deploy", "scripts", "verify-optional-observability.sh");
        var variantStart = script.IndexOf("verify_variant()", StringComparison.Ordinal);
        var variantBlock = script[variantStart..];

        Assert.Contains("--configfile \"$ROOT/NuGet.config\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("NuGet.Config", script, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Root, "NuGet.config")));
        Assert.Equal(2, variantBlock.Split("\"$DOTNET\" restore \"$PROJECT\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("--use-lock-file --force-evaluate", variantBlock, StringComparison.Ordinal);
        Assert.Contains("-p:RestoreLockedMode=false", variantBlock, StringComparison.Ordinal);
        Assert.Contains("--locked-mode", variantBlock, StringComparison.Ordinal);
        Assert.Contains("generated_lock_hash=", variantBlock, StringComparison.Ordinal);
        Assert.Contains("test \"$generated_lock_hash\" = \"$locked_lock_hash\"", variantBlock, StringComparison.Ordinal);
        foreach (var nonPortableLock in new[]
                 {
                     "packages.telemetry.lock.json",
                     "packages.web-analytics.lock.json",
                     "packages.both.lock.json"
                 })
        {
            Assert.False(File.Exists(Path.Combine(Root, "examples", "optional-observability", nonPortableLock)));
        }
    }

    [Fact]
    public void BackendComposeFilesUseThePersistentAppOwnedDataProtectionDirectory()
    {
        const string keyPath = "DataProtection__KeyPath=/app/data/dp-keys";
        foreach (var composeFile in new[]
                 {
                     "local.compose.yml",
                     "remote.compose.yml",
                     "remote-bluegreen.compose.yml"
                 })
        {
            var compose = Read("deploy", "compose", composeFile);
            Assert.Contains(keyPath, compose, StringComparison.Ordinal);
            Assert.Contains("dp-keys:/app/data/dp-keys", compose, StringComparison.Ordinal);
        }

        var dockerfile = Read("deploy", "docker", "Dockerfile.back");
        Assert.Contains("/app/data/dp-keys", dockerfile, StringComparison.Ordinal);
        Assert.Contains("chown -R app:app /app/logs /app/data", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void VpsSimulatorPersistsAgentStateAlongsideNestedDockerState()
    {
        var compose = Read("deploy", "compose", "vpssim.compose.yml");
        var dockerfile = Read("deploy", "docker", "Dockerfile.vpssim");

        Assert.Contains("vpssim-docker:/var/lib/docker", compose, StringComparison.Ordinal);
        Assert.Contains("vpssim-containerd:/var/lib/containerd", compose, StringComparison.Ordinal);
        Assert.Contains("vpssim-agent-state:/var/lib/aetheus-agent", compose, StringComparison.Ordinal);
        Assert.Contains("vpssim-apache:/etc/apache2", compose, StringComparison.Ordinal);
        Assert.Contains("vpssim-letsencrypt:/etc/letsencrypt", compose, StringComparison.Ordinal);
        Assert.Contains("vpssim-www:/var/www", compose, StringComparison.Ordinal);
        Assert.Contains("vpssim-agent-state:", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("down -v", compose, StringComparison.Ordinal);

        Assert.DoesNotContain("nodejs.org", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("node --version", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("npm --version", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("ARG NODE_VERSION", dockerfile, StringComparison.Ordinal);

        var bootstrap = Read("deploy", "docker", "vpssim-agent-bootstrap.sh");
        Assert.Contains("preserve_deploy_state", bootstrap, StringComparison.Ordinal);
        Assert.Contains("restore_deploy_state", bootstrap, StringComparison.Ordinal);
        Assert.Contains("/var/lib/aetheus-agent/aetheus-*", bootstrap, StringComparison.Ordinal);
        Assert.Contains("trap restore_deploy_state EXIT", bootstrap, StringComparison.Ordinal);
        Assert.Contains("[ -x /opt/aetheus-agent/Aetheus.Agent.Linux ]", bootstrap, StringComparison.Ordinal);
        Assert.Contains("rm -f /var/lib/vpssim-agent-installed", bootstrap, StringComparison.Ordinal);
        Assert.Contains("^https://(host\\.docker\\.internal|localhost|127\\.0\\.0\\.1)", bootstrap, StringComparison.Ordinal);
        Assert.Contains("set AGENT_TOKEN or VPSSIM_ADMIN_PASSWORD", bootstrap, StringComparison.Ordinal);
        Assert.Contains("AGENT_TOKEN=${VPSSIM_AGENT_TOKEN:-}", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("ADMIN_PASSWORD=\"${ADMIN_PASSWORD:-aetheus-dev-admin-pwd}\"", bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("VPSSIM_ADMIN_PASSWORD:-aetheus-dev-admin-pwd", compose, StringComparison.Ordinal);
        Assert.Contains("set -eu", bootstrap, StringComparison.Ordinal);
        Assert.Contains("if ./install-agent-linux.sh", bootstrap, StringComparison.Ordinal);
        Assert.Contains("RC=$?", bootstrap, StringComparison.Ordinal);
        Assert.True(
            bootstrap.IndexOf("preserve_deploy_state\n", StringComparison.Ordinal)
            < bootstrap.IndexOf("./install-agent-linux.sh", StringComparison.Ordinal));
        Assert.True(
            bootstrap.IndexOf("restore_deploy_state\n", bootstrap.IndexOf("./install-agent-linux.sh", StringComparison.Ordinal), StringComparison.Ordinal)
            > bootstrap.IndexOf("./install-agent-linux.sh", StringComparison.Ordinal));

        var launcher = Read("scripts", "launch-core.ps1");
        Assert.Contains("GitLight__CloneBaseUrl", launcher, StringComparison.Ordinal);
        Assert.Contains("src/Aetheus.Back/appsettings.Development.json", launcher, StringComparison.Ordinal);
        Assert.Contains("[string]$developmentAuth.AdminUser", launcher, StringComparison.Ordinal);
        Assert.Contains("[string]$developmentAuth.AdminPassword", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("else { \"admin\" }", launcher, StringComparison.Ordinal);

        var ensureBuildx = Read("deploy", "scripts", "ensure-buildx-builder.sh");
        Assert.Contains("docker buildx inspect \"$BUILDER\" --bootstrap", ensureBuildx, StringComparison.Ordinal);
        Assert.Contains("docker buildx rm --force \"$BUILDER\"", ensureBuildx, StringComparison.Ordinal);
        Assert.Contains("--driver docker-container", ensureBuildx, StringComparison.Ordinal);
        Assert.Contains("--bootstrap >/dev/null", ensureBuildx, StringComparison.Ordinal);
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
        // The boundary, not the project's name in it: a direct child of /var/www, refusing a deeper
        // path and any traversal. The `aetheus-` prefix this used to demand named the project rather
        // than bounding the path, and naming is the Variable Library's job (PLAN-006 lot 10).
        Assert.Contains("/var/www/*/*|*..*", publishVitrine, StringComparison.Ordinal);
        Assert.Contains("/var/www/?*", publishVitrine, StringComparison.Ordinal);
        Assert.Contains("readlink -m -- \"$WEB_ROOT\"", publishVitrine, StringComparison.Ordinal);
        Assert.Contains("Refusing publication through a symbolic-link web root", publishVitrine, StringComparison.Ordinal);
        Assert.Contains("Refusing to publish a vitrine containing unresolved", publishVitrine, StringComparison.Ordinal);
        Assert.DoesNotContain("grep -rc \"aetheus\\.example\" site/ || true", publishVitrine, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsLauncherReportsBackgroundDotnetFailuresWithoutGuiDialog()
    {
        var launcher = Read("scripts", "launch-core.ps1");
        const string guardedStart =
            "Start-Job -InitializationScript $dotnetJobInitialization -ScriptBlock";

        Assert.Contains("$env:DOTNET_DISABLE_GUI_ERRORS = \"1\"", launcher, StringComparison.Ordinal);
        Assert.Equal(
            launcher.Split("Start-Job", StringSplitOptions.None).Length - 1,
            launcher.Split(guardedStart, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Start-Job -ScriptBlock", launcher, StringComparison.Ordinal);
        var guardedInvocations = System.Text.RegularExpressions.Regex.Matches(
            launcher,
            "(?m)^\\s*dotnet (?:(?:watch )?run .+|\\(Join-Path \\$using:(?:backSnapshotDir|frontSnapshotHostDir|agentSnapshotDir) \"[^\"]+\\.dll\"\\)) 2>&1; \\\"##PROMEXIT##\\$LASTEXITCODE\\\"\\r?$").Count;
        Assert.Equal(launcher.Split(guardedStart, StringSplitOptions.None).Length - 1, guardedInvocations);
        Assert.Contains("dotnet (Join-Path $using:backSnapshotDir \"Aetheus.Back.dll\")", launcher, StringComparison.Ordinal);
        Assert.Contains("dotnet (Join-Path $using:frontSnapshotHostDir \"StaticServer.dll\")", launcher, StringComparison.Ordinal);
        Assert.Contains("dotnet (Join-Path $using:agentSnapshotDir \"Aetheus.Agent.Windows.dll\")", launcher, StringComparison.Ordinal);
        Assert.Contains("^##PROMEXIT##(-?\\d+)$", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("^##PROMEXIT##(\\d+)$", launcher, StringComparison.Ordinal);
        Assert.Contains("$backReady = Wait-ForEndpoint -url $backUrl", launcher, StringComparison.Ordinal);
        Assert.Contains("Server startup failed. Dumping captured output", launcher, StringComparison.Ordinal);
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

    /// <summary>
    /// A configuration key the deployment REQUIRES must be one the deployment PROVISIONS. Production
    /// refuses the <c>Auth:EncryptionKey</c> fallback for the bootstrap identity, but nothing wrote
    /// the dedicated key: the environment file is generated once and never rewritten, so every host
    /// installed before that requirement reached its blocking smoke and was refused an identity, with
    /// no route to ever satisfy it. The three halves are pinned together here: the backend reads the
    /// key, Compose passes it, and the preparation script both generates and back-fills it.
    /// </summary>
    [Fact]
    public void TheGitRunTokenKey_ReachesBothColours_AndAnAbsentValueStaysEmpty()
    {
        // R-464, key separation: clone tokens are signed with a key of their own instead of the data
        // encryption key, but only if the containers receive it. Nothing passed it, so setting it in
        // the environment file alone changed nothing. It belongs to the anchor both
        // colours merge, and defaults to empty: an environment file without it (release-fast or
        // revert-prod before the next prepare, the demo and the nightly) keeps the fallback key.
        var compose = Read("deploy", "compose", "remote-bluegreen.compose.yml");
        var backAnchor = compose.IndexOf("<<: &back-common", StringComparison.Ordinal);
        var backBlue = compose.IndexOf("container_name: ${APPNAME}-${ENV}-blue-back", StringComparison.Ordinal);
        Assert.True(backAnchor > 0 && backBlue > backAnchor);
        Assert.Contains(
            "- GitLight__RunTokenKey=${GIT_RUN_TOKEN_KEY:-}",
            compose[backAnchor..backBlue],
            StringComparison.Ordinal);
        Assert.Contains("<<: *back-common", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeploymentBootstrapIdentityKey_IsProvisioned_NotJustRequired()
    {
        var identity = Read("src", "Aetheus.Back", "Components", "Pipelines", "DeploymentBootstrapIdentity.cs");
        Assert.Contains("Deployment:BootstrapIdentityKey", identity, StringComparison.Ordinal);

        var compose = Read("deploy", "compose", "remote-bluegreen.compose.yml");
        Assert.Contains(
            "Deployment__BootstrapIdentityKey=${DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY}",
            compose,
            StringComparison.Ordinal);

        var prepare = Read("deploy", "scripts", "prod-deploy-prepare.sh");
        // Generated on a first deploy (R-248: declared by the versioned template the script renders)...
        var productionTemplate = Read("deploy", "env", "prod.env.sample");
        Assert.Contains(
            "\nDEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=#{SECRET_HEX_32}#\n",
            productionTemplate.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains("deploy/env/prod.env.sample", prepare, StringComparison.Ordinal);
        // ...drawn by the back-fill with the same recipe...
        Assert.Contains(
            "DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=$(openssl rand -hex 32)",
            prepare,
            StringComparison.Ordinal);
        // ...back-filled on every host whose environment file predates the requirement...
        Assert.Contains(
            "grep -q \"^DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=.\\+\" \"$ENV_FILE\"",
            prepare,
            StringComparison.Ordinal);
        // ...and verified present before the deployment proceeds.
        Assert.Contains("DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY; do", prepare, StringComparison.Ordinal);

        // The demo host runs the SAME compose file, so the same interpolation applies to it. Only the
        // production path provisioned the key, so on the demo it resolved to an empty string, the
        // deployed backend could not re-derive the throwaway smoke account, and the blocking probe was
        // refused with 401 on a healthy deployment. Nightly run 1204 failed exactly there.
        var demoPrepare = Read("deploy", "scripts", "nightly-demo-prepare.sh");
        Assert.Contains(
            "DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=$(openssl rand -hex 32)",
            demoPrepare,
            StringComparison.Ordinal);
        Assert.Contains(
            "grep -q \"^DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=.\\+\" \"$ENV_FILE\"",
            demoPrepare,
            StringComparison.Ordinal);
        Assert.Contains(
            "^DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=[0-9a-f]{64}$",
            demoPrepare,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// R-248: the production environment template is versioned, so it must never carry a secret. Every
    /// secret key is a generation marker, and no value anywhere in it looks like a generated secret.
    /// The byte-for-byte equivalence with the former inline heredoc is proved by
    /// <c>deploy/scripts/verify-prod-env-sample.sh</c>, which executes the script's own block.
    /// </summary>
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

    /// <summary>The directive block a LocationMatch opens, up to its closing tag.</summary>
    private static string Block(string conf, string locationPattern)
    {
        var start = conf.IndexOf($"<LocationMatch \"{locationPattern}\">", StringComparison.Ordinal);
        Assert.True(start >= 0, $"No LocationMatch for {locationPattern}; the cache policy is not where the guard reads it.");
        var end = conf.IndexOf("</LocationMatch>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"LocationMatch for {locationPattern} is never closed.");
        return conf[start..end];
    }

    private static string FindRepoRoot() => Aetheus.Back.Tests.Architecture.RepositoryScan.Root;
}
