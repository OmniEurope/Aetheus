// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// Every function a <c>type: aetheus</c> step can name. The control plane validates steps against it
/// at save time and the agent routes tasks by the same names, so it lives in Shared: a function both
/// sides do not agree on must fail to resolve, not run with a guessed contract.
/// </summary>
public static class PipelineFunctionCatalog
{
    /// <summary>The <c>type:</c> value of a step that runs a catalog function.</summary>
    public const string StepType = "aetheus";

    /// <summary>Detects which language toolchains the repository contains, so later steps can be
    /// conditioned on them. Port of the former <c>detect-project-languages.sh</c> script, with the same
    /// outputs.</summary>
    public static readonly PipelineFunctionDescriptor DetectProjectLanguages = new(
        "detect-project-languages",
        PipelineFunctionAccess.Build,
        "Detects the .NET, JavaScript/TypeScript, Python and Java sources of the repository and publishes one true/false variable per language.",
        [new PipelineFunctionParameter("root", Required: false, "Directory to scan, relative to the workspace. Defaults to the workspace root.")],
        ["HAS_DOTNET", "HAS_JAVASCRIPT", "HAS_PYTHON", "HAS_JAVA"]);

    /// <summary>Resolves the exact .NET SDK <c>global.json</c> pins, installing it in a non-root,
    /// version-isolated cache when the host does not resolve it. Port of
    /// <c>scripts/ensure-dotnet-sdk.sh</c>; the path the script printed is the <c>DOTNET</c>
    /// output.</summary>
    public static readonly PipelineFunctionDescriptor EnsureDotnetSdk = new(
        "ensure-dotnet-sdk",
        PipelineFunctionAccess.Build,
        "Resolves the exact .NET SDK pinned by global.json, installs it under $HOME/.aetheus/dotnet (or AETHEUS_DOTNET_ROOT) when the host does not provide it, and publishes the dotnet executable path.",
        [new PipelineFunctionParameter("global_json", Required: false, "Path of the SDK manifest, relative to the workspace. Defaults to global.json.")],
        ["DOTNET"]);

    /// <summary>Restores the workspace's JavaScript dependencies from the committed root
    /// <c>package-lock.json</c> with the pinned Node.js. Port of
    /// the former <c>restore-javascript-analysis-dependencies.sh</c> script, which publishes
    /// nothing.</summary>
    public static readonly PipelineFunctionDescriptor RestoreJavaScriptAnalysisDependencies = new(
        "restore-javascript-analysis-dependencies",
        PipelineFunctionAccess.Build,
        "Runs npm ci with the pinned Node.js against the committed root package-lock.json, lifecycle scripts disabled, retrying a failed restore twice.",
        [],
        []);

    /// <summary>Checks the formatting of the C# and Razor files changed since the delivery baseline
    /// (the whole solution whenever narrowing is not provably safe) and writes a SARIF log. Port of
    /// the former <c>run-affected-dotnet-format.sh</c> script, with the solution as a parameter instead of
    /// <c>Aetheus.slnx</c> and the same outputs.</summary>
    public static readonly PipelineFunctionDescriptor RunAffectedDotnetFormat = new(
        "run-affected-dotnet-format",
        PipelineFunctionAccess.Build,
        "Runs dotnet format whitespace and dotnet format style --verify-no-changes (formatting only, no analyzers) on the C# and Razor files changed since the delivery baseline, or on the whole solution when the change cannot be narrowed safely, writes .analysis-format/dotnet-format.sarif and publishes the mode and the findings status.",
        [
            new PipelineFunctionParameter("solution", Required: true, "Solution or project to format, relative to the workspace."),
            new PipelineFunctionParameter("mode", Required: false, "affected (default) or full. Defaults to AETHEUS_FORMAT_MODE."),
            new PipelineFunctionParameter("baseline", Required: false, "Full SHA the change is measured from. Defaults to DELIVERY_BASELINE_SOURCE_SHA; without a valid one the whole solution is checked."),
            new PipelineFunctionParameter("head", Required: false, "Full SHA of the checked-out change. Defaults to BUILD_SOURCEVERSION; without a valid one the whole solution is checked."),
            new PipelineFunctionParameter("dotnet", Required: false, "dotnet executable, for instance the DOTNET output of ensure-dotnet-sdk. Defaults to DOTNET, then dotnet on PATH."),
            new PipelineFunctionParameter("restore", Required: false, "true (default) restores the solution before the check; false reuses the restore of an earlier compilation of the same workspace, whose obj/ files a second restore would rewrite.")
        ],
        ["DOTNET_FORMAT_MODE", "DOTNET_FORMAT_GATE_STATUS"]);

    /// <summary>Composes the application version from the major and minor of the single
    /// <c>&lt;VersionPrefix&gt;</c> of <c>Directory.Build.props</c>, the release number and an
    /// environment suffix. Port of the former <c>app-version.sh</c> script; the version the script printed
    /// is the <c>APP_VERSION</c> output.</summary>
    public static readonly PipelineFunctionDescriptor AppVersion = new(
        "app-version",
        PipelineFunctionAccess.Build,
        "Composes <major>.<minor>.<release number><suffix> from the single <VersionPrefix> of the workspace's Directory.Build.props and publishes it.",
        [
            new PipelineFunctionParameter("run_number", Required: false, "Release number, digits only. Defaults to BUILD_RELEASENUMBER."),
            new PipelineFunctionParameter("suffix", Required: false, "Environment suffix appended as is ([0-9A-Za-z._-] only). Defaults to VERSION_SUFFIX, else none.")
        ],
        ["APP_VERSION"]);

    /// <summary>Derives the deployment identity from the owning project and agent: the image
    /// repositories named after the project and the agent's root-owned helper directory. Port of
    /// <c>deploy-identity.sh</c> (the heredoc program of the .pipeline YAML), whose shell variables become outputs.</summary>
    public static readonly PipelineFunctionDescriptor DeployIdentity = new(
        "deploy-identity",
        PipelineFunctionAccess.Build,
        "Publishes the project slug, the <slug>-back and <slug>-front image repositories and the validated agent helper directory.",
        [
            new PipelineFunctionParameter("project_name", Required: false, "Project the images are named after. Defaults to BUILD_PROJECTNAME."),
            new PipelineFunctionParameter("helpers_dir", Required: false, "Absolute directory of the agent's root-owned helpers. Defaults to AGENT_HELPERS_DIR, else /usr/local/lib/aetheus.")
        ],
        ["PROJECT_SLUG", "BACK_IMAGE_REPO", "FRONT_IMAGE_REPO", "AGENT_HELPERS_DIR"]);

    /// <summary>Requires the checkout to be the requested immutable revision and, in full Gitleaks
    /// mode, to hold the complete history (unshallowing it first). Port of
    /// the former <c>verify-immutable-source.sh</c> script, which publishes nothing.</summary>
    public static readonly PipelineFunctionDescriptor VerifyImmutableSource = new(
        "verify-immutable-source",
        PipelineFunctionAccess.Build,
        "Checks that HEAD is the requested revision and, in full mode, fetches the missing history from origin and refuses a shallow checkout.",
        [
            new PipelineFunctionParameter("head", Required: false, "Revision HEAD must be. Defaults to BUILD_SOURCEVERSION."),
            new PipelineFunctionParameter("mode", Required: false, "full requires the complete history; any other value only checks HEAD. Defaults to AETHEUS_GITLEAKS_MODE, else release-range.")
        ],
        []);

    /// <summary>Verifies restored image archives against the source revision and the digests of their
    /// delivery contract, then unpacks them into <c>.analysis-image/</c> for the image scanners. Port of
    /// the former <c>prepare-image-inputs.sh</c> script, with the archive names as a parameter
    /// instead of the Aetheus images.</summary>
    public static readonly PipelineFunctionDescriptor PrepareImageInputs = new(
        "prepare-image-inputs",
        PipelineFunctionAccess.Build,
        "Checks the restored source-commit, the delivery contract (schema 2, same source) and the SHA-256 of each image archive, then unpacks every archive into .analysis-image/ with its source-commit marker.",
        [
            new PipelineFunctionParameter("images", Required: true, "Image archive file names (.tar.gz), separated by spaces, as the delivery contract lists them."),
            new PipelineFunctionParameter("input", Required: false, "Directory holding the restored archives, source-commit and delivery-contract.json, relative to the workspace. Defaults to .security-image-input/.pipeline-artifacts."),
            new PipelineFunctionParameter("source", Required: false, "Revision the archives must be built from. Defaults to BUILD_SOURCEVERSION.")
        ],
        []);

    // PLAN-002 lot 1.6, assurance contract.

    /// <summary>Refuses a candidate that has no rollback-capable deployed baseline unless the bootstrap
    /// is explicitly allowed, before qualification. Port of
    /// the former <c>authorize-baseline-bootstrap.sh</c> script, which publishes nothing.</summary>
    public static readonly PipelineFunctionDescriptor AuthorizeBaselineBootstrap = new(
        "authorize-baseline-bootstrap",
        PipelineFunctionAccess.Build,
        "Fails when no deployed baseline exists (bootstrap) and the bootstrap is not explicitly allowed.",
        [
            new PipelineFunctionParameter("bootstrap", Required: false, "true when no deployed baseline exists, for instance the DELIVERY_BASELINE_BOOTSTRAP output of export-baseline-identity. Defaults to DELIVERY_BASELINE_BOOTSTRAP, else true."),
            new PipelineFunctionParameter("allow_bootstrap", Required: false, "true to accept a bootstrap. Defaults to ALLOW_BOOTSTRAP.")
        ],
        []);

    /// <summary>Publishes the source revision and the schema state of the deployed baseline from its
    /// restored delivery contract, or attests a bootstrap when none was restored. Port of
    /// the former <c>export-baseline-identity.sh</c> script, with the same outputs and the
    /// migrations directory as a parameter instead of the Aetheus one.</summary>
    public static readonly PipelineFunctionDescriptor ExportBaselineIdentity = new(
        "export-baseline-identity",
        PipelineFunctionAccess.Build,
        "Reads the restored delivery contract of the deployed baseline and publishes its source revision, schema fingerprint and base64 migration list (rebuilt from the Git tree of the deployed revision for a legacy contract), or DELIVERY_BASELINE_BOOTSTRAP=true when no contract was restored.",
        [
            new PipelineFunctionParameter("contract", Required: false, "Restored delivery contract, relative to the workspace. Defaults to .delivery-baseline/.pipeline-artifacts/delivery-contract.json."),
            new PipelineFunctionParameter("migrations_dir", Required: false, "Directory of the migration sources in the Git tree, used only to rebuild the schema state of a legacy contract without baseline.schemaAfter.")
        ],
        ["DELIVERY_BASELINE_BOOTSTRAP", "DELIVERY_BASELINE_SOURCE_SHA", "DELIVERY_BASELINE_SCHEMA_HASH", "DELIVERY_BASELINE_SCHEMA_MIGRATIONS_B64"]);

    /// <summary>Seals the analysis grades and the test gate outcomes of a qualification into the
    /// candidate assurance contract and publishes its overall grade. Port of
    /// <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/generate-candidate-assurance-contract.mjs</c>, writing the same contract and
    /// evidence files byte for byte.</summary>
    public static readonly PipelineFunctionDescriptor GenerateAssuranceContract = new(
        "generate-assurance-contract",
        PipelineFunctionAccess.Build,
        "Copies the single grade summary of each analysis directory into the evidence directory (or writes an Unavailable evidence when it is missing, ambiguous or not JSON), grades the test gate outcomes read from the run variables, seals everything with the artifact provenance into a schema 2 assurance contract (sha256 of its JSON) and publishes the overall grade. Fails on a summary or a provenance of another commit.",
        [
            new PipelineFunctionParameter("output", Required: true, "Assurance contract to write, relative to the workspace, for instance .pipeline-artifacts/assurance-contract.json."),
            new PipelineFunctionParameter("evidence_directory", Required: true, "Directory receiving one evidence file per analysis, relative to the workspace, for instance .pipeline-artifacts/assurance."),
            new PipelineFunctionParameter("source_sha", Required: true, "Full hexadecimal commit (40 or 64 characters) every summary and the provenance must belong to."),
            new PipelineFunctionParameter("quality", Required: true, "Directory searched for the quality-summary.json of the quality analysis, relative to the workspace."),
            new PipelineFunctionParameter("security", Required: true, "Directory searched for the security-summary.json of the security analysis, relative to the workspace."),
            new PipelineFunctionParameter("qa", Required: true, "Directory searched for the security-summary.json of the dynamic security analysis, relative to the workspace."),
            new PipelineFunctionParameter("provenance", Required: true, "artifact-provenance.json of the candidate, relative to the workspace."),
            new PipelineFunctionParameter("security_history", Required: false, "Directory of the security history analysis summary, relative to the workspace. Full qualification only, together with extended_qa."),
            new PipelineFunctionParameter("extended_qa", Required: false, "Directory of the extended dynamic security analysis summary, relative to the workspace. Full qualification only, together with security_history."),
            new PipelineFunctionParameter("profile", Required: false, "Qualification profile, light or full. Defaults to AETHEUS_ASSURANCE_PROFILE, else full."),
            new PipelineFunctionParameter("performance_enabled", Required: false, "true when the performance smoke is required, false when it is not run. Defaults to AETHEUS_ASSURANCE_PERFORMANCE_ENABLED; any other value seals it as Unavailable."),
            new PipelineFunctionParameter("allow_bootstrap", Required: false, "true to authorize Bootstrap compatibility in the contract. Defaults to AETHEUS_ASSURANCE_ALLOW_BOOTSTRAP."),
            new PipelineFunctionParameter("inapplicable_tests", Required: false, "Comma-separated test names the project cannot produce, sealed as unrequired NotRun. Defaults to AETHEUS_ASSURANCE_INAPPLICABLE_TESTS, else none.")
        ],
        ["CANDIDATE_ASSURANCE_GRADE"]);

    /// <summary>Verifies a candidate assurance contract against its artifact: seal, source commit,
    /// evidence hashes, grade consistency and the deployment threshold. Port of
    /// <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/verify-candidate-assurance-contract.mjs</c>, with the same refusals.</summary>
    public static readonly PipelineFunctionDescriptor VerifyAssuranceContract = new(
        "verify-assurance-contract",
        PipelineFunctionAccess.Build,
        "Fails unless the assurance contract (schema 1 or 2) belongs to the artifact's source commit, matches its seal, references evidence files inside the artifact whose hashes match, grades consistently, and reaches the minimum grade; requirement evidence also requires every analysis and required test to be available, deployment-readiness every required test to be Passed.",
        [
            new PipelineFunctionParameter("contract", Required: true, "Assurance contract, relative to the workspace, for instance .pipeline-artifacts/assurance-contract.json."),
            new PipelineFunctionParameter("source_commit", Required: true, "File holding the artifact's source commit, relative to the workspace, for instance .pipeline-artifacts/source-commit."),
            new PipelineFunctionParameter("minimum_grade", Required: true, "Lowest acceptable grade, A to F."),
            new PipelineFunctionParameter("requirement", Required: false, "none (default), evidence or deployment-readiness.")
        ],
        []);

    /// <summary>The production deployment gate on the delivery contract: sealed identity, release
    /// version, artifact digests and the live schema state. Port of
    /// <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/verify-delivery-promotion.mjs</c>, with the same refusals.</summary>
    public static readonly PipelineFunctionDescriptor VerifyDeliveryPromotion = new(
        "verify-delivery-promotion",
        PipelineFunctionAccess.Build,
        "Fails unless the candidate delivery contract is a schema 2 contract whose sealed identity (sha256 of its canonical JSON) and release version hold, whose artifacts match their digests, and whose proven schema states include the live production schema: the state before or after its migrations, an empty or identical database for a bootstrap, or the state of the release production runs when that release's own contract proves the candidate is its predecessor.",
        [
            new PipelineFunctionParameter("candidate_contract", Required: true, "Candidate delivery contract, relative to the workspace, for instance .pipeline-artifacts/delivery-contract.json."),
            new PipelineFunctionParameter("production_schema", Required: true, "Migration history read from the live database (one MigrationId per line), relative to the workspace, or - when production has no database."),
            new PipelineFunctionParameter("artifact_directory", Required: true, "Directory holding the artifacts the contract seals, relative to the workspace, for instance .pipeline-artifacts."),
            new PipelineFunctionParameter("live_contract", Required: false, "Delivery contract of the release production runs, relative to the workspace, or - (the default) when none is known.")
        ],
        []);

    /// <summary>Writes the deterministic SPDX 2.3 SBOM of a package from its dependency list. Port of
    /// the former <c>generate-package-sbom.mjs</c> script, writing the same document byte for byte.</summary>
    public static readonly PipelineFunctionDescriptor GeneratePackageSbom = new(
        "generate-package-sbom",
        PipelineFunctionAccess.Build,
        "Writes the deterministic SPDX 2.3 JSON SBOM of a package: the package and its dependencies (from the JSON of dotnet list package --include-transitive for nuget, from package.json for npm), one per SPDX id, sorted, each with its purl, and the DESCRIBES and DEPENDS_ON relationships, created at the source date epoch.",
        [
            new PipelineFunctionParameter("ecosystem", Required: true, "nuget or npm."),
            new PipelineFunctionParameter("input", Required: true, "Dependency JSON, relative to the workspace: dotnet list package --format json output for nuget, package.json for npm."),
            new PipelineFunctionParameter("name", Required: true, "Package name, for instance Aetheus.Telemetry or @aetheus/web-analytics."),
            new PipelineFunctionParameter("version", Required: true, "Package version."),
            new PipelineFunctionParameter("source_commit", Required: true, "Full hexadecimal source commit (40 or 64 characters)."),
            new PipelineFunctionParameter("output", Required: true, "SPDX JSON file to write, relative to the workspace; its directory must exist."),
            new PipelineFunctionParameter("source_date_epoch", Required: false, "Creation time in seconds since the epoch, the source commit timestamp. Defaults to SOURCE_DATE_EPOCH; 0 or unset is refused.")
        ],
        []);

    /// <summary>Issues or renews every requested certificate through the agent's root-owned certbot
    /// helpers, then requires each to name all its hosts and to stay valid for the minimum number of
    /// days. Port of the former <c>ensure-tls-certificates.sh</c> script, with the certificate groups as a
    /// parameter instead of arguments.</summary>
    public static readonly PipelineFunctionDescriptor EnsureTlsCertificates = new(
        "ensure-tls-certificates",
        PipelineFunctionAccess.Deployment,
        "Issues a missing or incomplete certificate, renews one that expires too soon (issuing it again through the web root when renewal fails), then fails unless every certificate names all its hosts and stays valid for min_days.",
        [
            new PipelineFunctionParameter("hosts", Required: true, "Certificate groups separated by spaces, each NAME[,ALIAS...]: the first name is the lineage. A trailing comma (an empty alias) is ignored."),
            new PipelineFunctionParameter("min_days", Required: false, "Minimum remaining lifetime in days. Defaults to TLS_MIN_DAYS, else 30."),
            new PipelineFunctionParameter("live_dir", Required: false, "Directory of the certificate lineages. Defaults to TLS_LIVE_DIR, else /etc/letsencrypt/live."),
            new PipelineFunctionParameter("email", Required: false, "ACME account email handed to the issue helper. Defaults to AETHEUS_CERTBOT_EMAIL, else none."),
            new PipelineFunctionParameter("helpers_dir", Required: false, "Absolute directory of the agent's root-owned helpers. Defaults to AGENT_HELPERS_DIR, else /usr/local/lib/aetheus.")
        ],
        []);

    /// <summary>Lets Apache traverse to an ACME challenge web root kept under the agent work
    /// directory. Port of the former <c>grant-acme-challenge-traversal.sh</c> script.</summary>
    public static readonly PipelineFunctionDescriptor GrantAcmeChallengeTraversal = new(
        "grant-acme-challenge-traversal",
        PipelineFunctionAccess.Deployment,
        "Adds the others-execute bit to the agent work directory when the ACME web root lives under it, then makes the web root world-readable where the agent may (best effort).",
        [new PipelineFunctionParameter("webroot", Required: false, "ACME challenge web root. Defaults to ACME_WEBROOT.")],
        []);

    /// <summary>Removes every container, volume and network of a disposable blue-green project and its
    /// deployment state, refusing anything that names production. Port of
    /// the former <c>bluegreen-host-reset.sh</c> script.</summary>
    public static readonly PipelineFunctionDescriptor BlueGreenHostReset = new(
        "bluegreen-host-reset",
        PipelineFunctionAccess.Deployment,
        "Resets a disposable (*-demo or *-nightly) Compose project: removes its containers, volumes, networks and listed images, deletes its environment, secrets and blue-green state, and proves nothing remains.",
        [
            new PipelineFunctionParameter("state_dir", Required: false, "State directory, a direct child of the workspace. Defaults to STATE_DIR."),
            new PipelineFunctionParameter("env_file", Required: false, "Environment file inside the state directory. Defaults to ENV_FILE."),
            new PipelineFunctionParameter("secrets_file", Required: false, "Secrets file inside the state directory. Defaults to SECRETS_FILE."),
            new PipelineFunctionParameter("compose_project", Required: false, "Compose project, ending in -demo or -nightly. Defaults to COMPOSE_PROJECT."),
            new PipelineFunctionParameter("remove_images", Required: false, "Image repositories to remove as well, separated by spaces, each starting with <compose_project>-. Defaults to none.")
        ],
        []);

    /// <summary>Removes the unprotected, older tags of managed image repositories after a deployment,
    /// keeping every image a container references and the most recent ones. Port of
    /// the former <c>retain-docker-images.sh</c> script, with the repositories as a parameter instead of
    /// arguments.</summary>
    public static readonly PipelineFunctionDescriptor RetainDockerImages = new(
        "retain-docker-images",
        PipelineFunctionAccess.Deployment,
        "Keeps the images any container references and the most recent tags of each managed repository, removes the other tags (or lists them in dry run), serialized with every other image retention on the host.",
        [
            new PipelineFunctionParameter("repositories", Required: true, "Managed image repositories, separated by spaces."),
            new PipelineFunctionParameter("keep", Required: false, "Tags kept per repository, protected ones included, at least 2. Defaults to AETHEUS_IMAGE_RETENTION_KEEP, else 2."),
            new PipelineFunctionParameter("dry_run", Required: false, "true lists what would be removed without removing it. Defaults to AETHEUS_IMAGE_RETENTION_DRY_RUN, else false.")
        ],
        []);

    /// <summary>Bounds a named Buildx builder's cache. Port of
    /// the former <c>prune-qa-buildx-cache.sh</c> script.</summary>
    public static readonly PipelineFunctionDescriptor PruneQaBuildxCache = new(
        "prune-qa-buildx-cache",
        PipelineFunctionAccess.Build,
        "Runs docker buildx prune on one named builder with a reserved space, a maximum used space and a minimum free space, never unscoped.",
        [
            new PipelineFunctionParameter("builder", Required: false, "Buildx builder to prune. Defaults to AETHEUS_BUILDX_BUILDER, else BUILDX_BUILDER."),
            new PipelineFunctionParameter("max_cache_gib", Required: false, "Maximum cache size in Go. Defaults to AETHEUS_QA_BUILDX_MAX_CACHE_GIB, else 32."),
            new PipelineFunctionParameter("reserved_cache_gib", Required: false, "Cache kept in any case, in Go. Defaults to AETHEUS_QA_BUILDX_RESERVED_CACHE_GIB, else 20."),
            new PipelineFunctionParameter("min_free_gib", Required: false, "Free disk space to keep, in Go. Defaults to AETHEUS_QA_BUILDX_MIN_FREE_GIB, else 20.")
        ],
        []);

    /// <summary>Removes the Compose resources left by earlier runs' per-run stacks. Port of
    /// the former <c>prune-stale-qa-compose.sh</c> script, with the project prefix as a parameter instead
    /// of <c>aetheus-qa</c>.</summary>
    public static readonly PipelineFunctionDescriptor PruneStaleQaCompose = new(
        "prune-stale-qa-compose",
        PipelineFunctionAccess.Build,
        "Removes the containers, networks and volumes of every <project_prefix>-<run> and <project_prefix>-rollback-<run> Compose project of an earlier run, keeping the current and newer runs.",
        [
            new PipelineFunctionParameter("project_prefix", Required: true, "Prefix of the per-run Compose projects, for instance myapp-qa."),
            new PipelineFunctionParameter("run_id", Required: false, "Current run id, digits only. Defaults to BUILD_BUILDID.")
        ],
        []);

    // Qualification N/N-1 (PLAN-002 lot 1.5).

    /// <summary>Records the host's storage after a successful QA (Buildx cache, root filesystem) in a
    /// ledger of the agent's work directory, refuses to record while a QA Compose stack of this run or an
    /// older one survives, and verifies the cache plateau once five samples exist. Port of
    /// the former <c>record-storage-evidence.sh</c> script, with the QA stacks named after the project
    /// instead of <c>aetheus-qa-</c>; it publishes nothing.</summary>
    public static readonly PipelineFunctionDescriptor RecordStorageEvidence = new(
        "record-storage-evidence",
        PipelineFunctionAccess.Build,
        "Measures the Buildx builder's cache and the root filesystem, refuses while a <project>-qa-[rollback-]<n> Compose stack of this run or an older one remains, appends the sample to <AETHEUS_AGENT_WORK_DIRECTORY>/storage-evidence/<scope>-v2.tsv and, from five samples of the builder on, fails when the cache exceeds its budget or has not reached a plateau.",
        [
            new PipelineFunctionParameter("scope", Required: false, "Ledger name ([a-zA-Z0-9_.-]). Defaults to qa."),
            new PipelineFunctionParameter("run_id", Required: false, "Run the sample belongs to ([a-zA-Z0-9_.-]); a numeric id also tells newer QA runs apart. Defaults to BUILD_BUILDID."),
            new PipelineFunctionParameter("builder", Required: false, "Buildx builder to inventory. Defaults to AETHEUS_BUILDX_BUILDER, then BUILDX_BUILDER."),
            new PipelineFunctionParameter("project_name", Required: false, "Project the QA Compose stacks are named after (<slug>-qa-[rollback-]<n>). Defaults to BUILD_PROJECTNAME."),
            new PipelineFunctionParameter("max_cache_gib", Required: false, "Cache budget in Go, a non-negative integer. Defaults to AETHEUS_STORAGE_MAX_CACHE_GIB, else 80."),
            new PipelineFunctionParameter("plateau_growth_gib", Required: false, "Largest cache variation over the last three samples, in Go. Defaults to AETHEUS_STORAGE_PLATEAU_GROWTH_GIB, else 5.")
        ],
        []);

    /// <summary>Publishes the compatibility attestation of a QA run (mode, V-1 tested, verdict) and the
    /// five N/N-1 gate statuses it is graded from, normalized to 0 or 1. Port of
    /// the former <c>persist-compatibility-mode.sh</c> script, with the same inputs; its three
    /// attestation outputs, PascalCase in the script, are renamed in upper snake case.</summary>
    public static readonly PipelineFunctionDescriptor PersistCompatibilityMode = new(
        "persist-compatibility-mode",
        PipelineFunctionAccess.Build,
        "Reads the five QA gate statuses (the workspace's .qa-<gate>-gate-status file, else the QA_<GATE>_GATE_STATUS variable, else 1), the .qa-bootstrap-mode marker and the .qa-previous-commit of the workspace, and publishes the statuses normalized to 0 or 1 with COMPATIBILITY_MODE, PREVIOUS_VERSION_TESTED and COMPATIBILITY_VERDICT.",
        [],
        [
            "QA_CURRENT_INTEGRATION_GATE_STATUS", "QA_CURRENT_E2E_GATE_STATUS", "QA_PREVIOUS_SMOKE_GATE_STATUS",
            "QA_PREVIOUS_INTEGRATION_GATE_STATUS", "QA_PREVIOUS_E2E_GATE_STATUS",
            "COMPATIBILITY_MODE", "PREVIOUS_VERSION_TESTED", "COMPATIBILITY_VERDICT"
        ]);

    // PLAN-002 lot 1.8, option c: the protected checks of the release-fast and deploy-prod scripts.

    /// <summary>Refuses an idle frontend that is not safe to put behind Apache: no HTML, unresolved
    /// asset placeholders, a Blazor bootstrap script it names but does not serve, missing or
    /// incompatible OmniEurope.Blazor assets, a cacheable index. Port of the idle-frontend checks of
    /// <c>release-fast-deploy.sh</c> (the heredoc program of the .pipeline YAML) (incident 2247).</summary>
    public static readonly PipelineFunctionDescriptor VerifyIdleFrontend = new(
        "verify-idle-frontend",
        PipelineFunctionAccess.Deployment,
        "Fetches the idle frontend's index and fails unless it is HTML without unresolved '#[' asset placeholders, names a Blazor bootstrap script it serves, references a compatible OmniEurope.Blazor stylesheet and interop script, and is sent with Cache-Control no-cache and not no-store (revalidated on every use, still eligible for the browser's back/forward cache).",
        [new PipelineFunctionParameter("url", Required: true, "Base URL of the idle frontend, for instance http://127.0.0.1:<idle front port>/.")],
        []);

    /// <summary>Closes a fast deployment transaction that went live but was never closed, and refuses
    /// any other leftover transaction. Port of the leftover-transaction guard of
    /// <c>release-fast-deploy.sh</c> (the heredoc program of the .pipeline YAML) (run 2240).</summary>
    public static readonly PipelineFunctionDescriptor SettleFastReleaseTransaction = new(
        "settle-fast-release-transaction",
        PipelineFunctionAccess.Deployment,
        "Reads the live colour and the fast-deployment-transaction directory next to the environment file: a transaction committed on the host whose candidate is the live colour (and whose commit is the recorded one) is closed, any other leftover transaction is refused.",
        [new PipelineFunctionParameter("env_file", Required: false, "Production environment file; its directory holds live-color, source-commit and the transaction. Defaults to ENV_FILE.")],
        []);

    /// <summary>Refuses a production state directory of the wrong shape and every secret-zero state the
    /// preparation would refuse, before it touches anything. Port of the state-directory and
    /// secret-zero refusals of <c>prod-deploy-prepare.sh</c> (the heredoc program of the .pipeline YAML).</summary>
    public static readonly PipelineFunctionDescriptor VerifyProductionState = new(
        "verify-production-state",
        PipelineFunctionAccess.Deployment,
        "Requires the environment file's directory to be a real direct child of /var/lib (and the declared state directory), refuses symbolic environment files, canonical and legacy environments that differ, and a missing environment file while persistent Compose state exists without a dated backup to restore.",
        [
            new PipelineFunctionParameter("env_file", Required: false, "Production environment file. Defaults to ENV_FILE."),
            new PipelineFunctionParameter("state_dir", Required: false, "Declared state directory, which must be the environment file's directory. Defaults to STATE_DIR, else not checked."),
            new PipelineFunctionParameter("legacy_env_file", Required: false, "Environment file of the former layout. Defaults to LEGACY_ENV_FILE, else none."),
            new PipelineFunctionParameter("compose_project", Required: false, "Compose project whose volumes and containers count as persistent state. Defaults to COMPOSE_PROJECT; required only when the environment file is missing.")
        ],
        []);

    /// <summary>Requires the prepared production environment to hold every listed key with a value and
    /// a dated recovery copy. Port of the required-secret check of
    /// <c>prod-deploy-prepare.sh</c> (the heredoc program of the .pipeline YAML).</summary>
    public static readonly PipelineFunctionDescriptor VerifyProductionSecrets = new(
        "verify-production-secrets",
        PipelineFunctionAccess.Deployment,
        "Fails unless the environment file is a regular file holding a non-empty KEY= line for every listed key, and a dated copy <file name>-YYYYMMDDTHHMMSSZ exists next to it.",
        [
            new PipelineFunctionParameter("keys", Required: true, "Keys that must have a value, separated by spaces."),
            new PipelineFunctionParameter("env_file", Required: false, "Production environment file. Defaults to ENV_FILE.")
        ],
        []);

    // The programs of the production preparation, the nightly and demo preparations and the vitrine
    // publication that lived as heredocs of the .pipeline YAML, tested in C# like any other function.

    /// <summary>Gives the environment file a key it lacks: 32 random bytes as hexadecimal, drawn once and
    /// never replaced. Port of the <c>DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY</c> and <c>GIT_RUN_TOKEN_KEY</c>
    /// (R-464) back-fill blocks of <c>prod-deploy-prepare.sh</c>.</summary>
    public static readonly PipelineFunctionDescriptor BackfillEnvSecret = new(
        "backfill-env-secret",
        PipelineFunctionAccess.Deployment,
        "When the environment file has no non-empty KEY= line, draws 32 random bytes as 64 lowercase hexadecimal characters, keeps a dated copy <file name>-YYYYMMDDTHHMMSSZ next to the file and writes every other line plus KEY=value through a mode 600 temporary copy (an empty KEY= line is dropped, not duplicated). A value already present is never replaced.",
        [
            new PipelineFunctionParameter("key", Required: true, "Environment key to back-fill: upper case, digits and underscores, starting with a letter."),
            new PipelineFunctionParameter("env_file", Required: false, "Production environment file. Defaults to ENV_FILE.")
        ],
        []);

    /// <summary>Refreshes the public URL lines of an environment file without touching a secret. Port of
    /// <c>reconcile-env-urls.sh</c>.</summary>
    public static readonly PipelineFunctionDescriptor ReconcileEnvUrls = new(
        "reconcile-env-urls",
        PipelineFunctionAccess.Deployment,
        "Validates every KEY=URL pair (upper case key, https://host[:port] with nothing else), then rewrites the lines of those keys in place or appends them, keeping a dated copy <file name>-YYYYMMDDTHHMMSSZ of the file as it was and replacing it with mode 600. A pair already holding the value changes nothing; when no pair changes the file and its directory are left as they are.",
        [
            new PipelineFunctionParameter("urls", Required: true, "KEY=URL pairs, separated by spaces, for instance API_BASE_URL=https://api.example.org."),
            new PipelineFunctionParameter("env_file", Required: false, "Production environment file. Defaults to ENV_FILE.")
        ],
        []);

    /// <summary>Creates the production environment file on the first deploy from the versioned template.
    /// Port of the first-deploy block of <c>prod-deploy-prepare.sh</c> and of
    /// <c>render-env-sample.sh</c>.</summary>
    public static readonly PipelineFunctionDescriptor RenderEnvSample = new(
        "render-env-sample",
        PipelineFunctionAccess.Deployment,
        "Does nothing when the environment file exists: its secrets are never regenerated. Otherwise renders the versioned template (KEY=VALUE lines; #{PUBLIC_API_URL}#, #{PUBLIC_APP_URL}#, #{APP_VERSION}# and #{SECRET_HEX_<N>}# as N random bytes in hexadecimal, N at least 16) and writes the result with mode 600 without overwriting a file another deployment created meanwhile. A refused template writes nothing.",
        [
            new PipelineFunctionParameter("sample", Required: true, "Environment template, relative to the workspace, for instance deploy/env/prod.env.sample."),
            new PipelineFunctionParameter("env_file", Required: false, "Environment file to create. Defaults to ENV_FILE."),
            new PipelineFunctionParameter("public_api_url", Required: false, "Value of #{PUBLIC_API_URL}#. Defaults to PUBLIC_API_URL."),
            new PipelineFunctionParameter("public_app_url", Required: false, "Value of #{PUBLIC_APP_URL}#. Defaults to PUBLIC_APP_URL."),
            new PipelineFunctionParameter("app_version", Required: false, "Value of #{APP_VERSION}#. Defaults to APP_VERSION.")
        ],
        []);

    /// <summary>Renders the versioned blue-green Apache upstream template. Port of
    /// <c>render-apache-upstream.sh</c>.</summary>
    public static readonly PipelineFunctionDescriptor RenderApacheUpstream = new(
        "render-apache-upstream",
        PipelineFunctionAccess.Deployment,
        "Renders .pipeline/configs/apache/aetheus-upstream.conf. With define_only it resolves only the Define name into a copy local to the run, which must keep its port placeholders. Otherwise it fills the ports and the colour and writes the file named by UPSTREAM_CONF (a file of a sites-available directory, no traversal); any token left, any invalid colour or port, or a destination other than UPSTREAM_CONF is refused and writes nothing.",
        [
            new PipelineFunctionParameter("template", Required: true, "Template path (relative to the workspace); it must end with /.pipeline/configs/apache/aetheus-upstream.conf."),
            new PipelineFunctionParameter("output", Required: true, "File to write; in full mode it must equal UPSTREAM_CONF."),
            new PipelineFunctionParameter("define_only", Required: false, "true to resolve only the Define name. Defaults to false."),
            new PipelineFunctionParameter("front_port", Required: false, "Frontend port, 1 to 65535. Required unless define_only is true."),
            new PipelineFunctionParameter("back_port", Required: false, "Backend port, 1 to 65535. Required unless define_only is true."),
            new PipelineFunctionParameter("active_color", Required: false, "blue or green. Required unless define_only is true."),
            new PipelineFunctionParameter("define", Required: false, "Apache Define name: upper case, digits and underscores, starting with a letter. Defaults to UPSTREAM_DEFINE."),
            new PipelineFunctionParameter("upstream_conf", Required: false, "Destination the environment expects in full mode. Defaults to UPSTREAM_CONF.")
        ],
        []);

    /// <summary>Publishes a static site into a dedicated web root under /var/www. Port of
    /// <c>publish-vitrine.sh</c>.</summary>
    public static readonly PipelineFunctionDescriptor PublishVitrine = new(
        "publish-vitrine",
        PipelineFunctionAccess.Deployment,
        "Refuses a missing domain, a web root that is not one directory under /var/www (traversal, links resolved, or itself a symbolic link) and a missing or symbolic-link source, writes the domain over the placeholder in every .html, .xml, .txt and .json file, refuses a source that still holds the placeholder, then swaps the site in beside a backup that is put back if the swap fails.",
        [
            new PipelineFunctionParameter("domain", Required: true, "Public domain written over the placeholder."),
            new PipelineFunctionParameter("web_root", Required: true, "Web root, exactly one directory under /var/www."),
            new PipelineFunctionParameter("placeholder", Required: true, "Placeholder text to replace with the domain, for instance aetheus.example."),
            new PipelineFunctionParameter("source_dir", Required: false, "Directory holding the site, relative to the workspace. Defaults to site.")
        ],
        []);

    // PLAN-002 lot 8: the in-house SAST rules are tested where they are written.

    /// <summary>Runs <c>opengrep scan --test</c> with the repository's in-house rules on their test
    /// corpus, with the OpenGrep binary and sandbox of the SAST scan (manifest download pinned by
    /// SHA-256, runtime image pinned by digest, no network, read-only mounts).</summary>
    public static readonly PipelineFunctionDescriptor OpenGrepRuleTests = new(
        "opengrep-rule-tests",
        PipelineFunctionAccess.Build,
        "Runs opengrep scan --test with the rule files of the rules directory on the test corpus, in the SAST scanner's sandbox (binary of scanner-manifest.json checked against its SHA-256, runtime image pinned by digest, no network, read-only mounts), and fails on any rule test that does not pass, any rule file without a test, any invalid rule, or when no rule test ran.",
        [
            new PipelineFunctionParameter("rules", Required: false, "Directory of the OpenGrep rule files, relative to the workspace. Defaults to .aetheus/security-rules/opengrep."),
            new PipelineFunctionParameter("tests", Required: false, "Directory of the annotated test files (ruleid: and ok: comments), relative to the workspace. Defaults to tests/security-rules.")
        ],
        []);

    // PLAN-002 lot 1.10: the scripts the CI, QA, extended QA and quality pipelines still called (R3-66).

    /// <summary>Resolves the exact Node.js the toolchain pins, installing it under
    /// <c>$HOME/.aetheus</c> when the host does not provide it. Port of
    /// <c>ensure-node-runtime.sh</c> (the heredoc program of the .pipeline YAML); the path the script printed is the <c>NODE</c>
    /// output and its directory <c>NODE_BIN_DIR</c>.</summary>
    public static readonly PipelineFunctionDescriptor EnsureNodeRuntime = new(
        "ensure-node-runtime",
        PipelineFunctionAccess.Build,
        "Resolves the pinned Node.js (the host node when it reports exactly that version, else the official Linux x64 archive checked against its SHA-256 and installed under $HOME/.aetheus) and publishes the node executable and its directory.",
        [],
        ["NODE", "NODE_BIN_DIR"]);

    /// <summary>Names the disposable QA stack and its database after the owning project and the run.
    /// Port of the former <c>qa-stack-identity.sh</c> script, whose shell variables and
    /// <c>qa_stack</c> helper become outputs.</summary>
    public static readonly PipelineFunctionDescriptor QaStackIdentity = new(
        "qa-stack-identity",
        PipelineFunctionAccess.Build,
        "Publishes the project slug (QA_APP), the PostgreSQL role and database of the QA stack (QA_DB_USER, QA_DB_NAME) and the Compose project <slug>-qa-rollback-<run> (QA_STACK).",
        [
            new PipelineFunctionParameter("run_id", Required: false, "Run the stack belongs to, digits only. Defaults to BUILD_BUILDID."),
            new PipelineFunctionParameter("project_name", Required: false, "Project the stack is named after. Defaults to BUILD_PROJECTNAME.")
        ],
        ["QA_APP", "QA_DB_USER", "QA_DB_NAME", "QA_STACK"]);

    /// <summary>Copies the agent release a backend image serves out of the image and verifies its
    /// manifest (commit, protocol policy, <c>ai.run</c> capability) and every archive's size and
    /// SHA-256. Port of the former <c>extract-agent-release-from-image.sh</c> script, with the directory
    /// inside the image as an optional parameter.</summary>
    public static readonly PipelineFunctionDescriptor ExtractAgentRelease = new(
        "extract-agent-release",
        PipelineFunctionAccess.Build,
        "Copies the agent release directory out of a backend image (docker create, docker cp), then fails unless agent-release-manifest.json names the expected commit, satisfies the protocol policy, publishes the ai.run capability and matches every archive's size and SHA-256; manifest-only keeps the manifest alone.",
        [
            new PipelineFunctionParameter("image", Required: true, "Backend image to read."),
            new PipelineFunctionParameter("source", Required: false, "Directory of the agent release inside the image. Defaults to /app/wwwroot/downloads/."),
            new PipelineFunctionParameter("output", Required: true, "Directory the release is copied to, relative to the workspace and inside it; it is emptied first."),
            new PipelineFunctionParameter("commit", Required: false, "Commit the manifest must name, hexadecimal. Defaults to BUILD_SOURCEVERSION."),
            new PipelineFunctionParameter("mode", Required: false, "full (default) keeps the archives; manifest-only keeps the manifest alone."),
            new PipelineFunctionParameter("protocol_policy", Required: false, "upgrade-bridge (default) requires protocols 1-2; historical-compatible also accepts 2-2.")
        ],
        []);

    /// <summary>Proves the schema path a delivery contract declares against the migration histories
    /// read from a live database. Port of the former <c>verify-schema-contract.mjs</c> script (and the
    /// fingerprint of <c>schema-fingerprint.mjs</c>).</summary>
    public static readonly PipelineFunctionDescriptor VerifySchemaContract = new(
        "verify-schema-contract",
        PipelineFunctionAccess.Build,
        "Fingerprints the migration history read after migrating to V (and, unless the contract is a bootstrap, the one V-1 ran on) and fails unless they equal the contract's baseline.schemaAfter and baseline.schemaBefore.",
        [
            new PipelineFunctionParameter("after", Required: true, "Migration history read after migrating to V (one MigrationId per line), relative to the workspace."),
            new PipelineFunctionParameter("before", Required: false, "Migration history V-1 ran on, relative to the workspace; a missing or empty file means none was read."),
            new PipelineFunctionParameter("contract", Required: false, "Delivery contract, relative to the workspace. Defaults to .pipeline-artifacts/delivery-contract.json.")
        ],
        []);

    /// <summary>Runs a retained N-1 agent release against the deployed backend: enrollment and
    /// heartbeat, task claim and start, result publication, with a JSON attestation. Port of
    /// the former <c>verify-nminus1-agent-contract.sh</c> script; its exit code 10 (a compatibility
    /// finding) is the status 1 of <c>AGENT_CONTRACT_STATUS</c>.</summary>
    public static readonly PipelineFunctionDescriptor VerifyNMinusOneAgentContract = new(
        "verify-nminus1-agent-contract",
        PipelineFunctionAccess.Build,
        "Verifies the retained Linux x64 agent archive, starts it against a loopback backend with a one-hour registration token, waits for its enrollment, then for a task it must claim, run and report, writes the attestation and publishes AGENT_CONTRACT_STATUS (0 passed, 1 compatibility finding).",
        [
            new PipelineFunctionParameter("backend_url", Required: true, "Loopback backend URL, http://127.0.0.1:<port> or http://localhost:<port>."),
            new PipelineFunctionParameter("admin_password_file", Required: true, "File holding the admin password, relative to the workspace."),
            new PipelineFunctionParameter("agent_release", Required: true, "Directory of the retained agent release (agent-release-manifest.json and archives), relative to the workspace."),
            new PipelineFunctionParameter("expected_commit", Required: true, "Commit the manifest must name, hexadecimal."),
            new PipelineFunctionParameter("evidence", Required: true, "Attestation file to write, relative to the workspace.")
        ],
        ["AGENT_CONTRACT_STATUS"]);

    /// <summary>Runs a prebuilt .NET integration test project of a restored source tree and grades it
    /// from its TRX. Port of the former <c>run-qa-integration-suite.sh</c> script; its exit code 10 (the
    /// suite ran and recorded failures) is the status 1 of <c>INTEGRATION_SUITE_STATUS</c>.</summary>
    public static readonly PipelineFunctionDescriptor RunQaIntegrationSuite = new(
        "run-qa-integration-suite",
        PipelineFunctionAccess.Build,
        "Resolves the SDK global.json pins, rebases the static web assets manifest onto the source tree, restores the locked graph when the restored package folder is absent, runs the prebuilt test project (Microsoft.Testing.Platform or VSTest, as the tested global.json selects) into .qa-test-results/<label>/integration.trx, classifies it, records the failed test names and publishes INTEGRATION_SUITE_STATUS (0 clean, 1 findings).",
        [
            new PipelineFunctionParameter("source_root", Required: true, "Restored source tree to test, relative to the workspace."),
            new PipelineFunctionParameter("label", Required: true, "Result label ([a-zA-Z0-9_-]), the directory under .qa-test-results."),
            new PipelineFunctionParameter("test_project", Required: true, "Directory of the test project under the source tree, holding <name>.csproj."),
            new PipelineFunctionParameter("static_web_assets_manifest", Required: false, "Static web assets runtime manifest of the prebuilt tests, relative to the source tree; rebased before the run when set."),
            new PipelineFunctionParameter("static_web_assets_project", Required: false, "Project directory the manifest's content roots belong to, relative to the source tree, for instance src/MyApp. Required with static_web_assets_manifest.")
        ],
        ["INTEGRATION_SUITE_STATUS"]);

    /// <summary>Proves that a deployed backend authenticates and reads through its migrated database:
    /// health, admin login, permissions and one page of projects. Port of
    /// the former <c>run-qa-deployed-integration-suite.sh</c> script; its exit code 10 is the status 1 of
    /// <c>DEPLOYED_PROBE_STATUS</c>.</summary>
    public static readonly PipelineFunctionDescriptor RunQaDeployedProbes = new(
        "run-qa-deployed-probes",
        PipelineFunctionAccess.Build,
        "Requires /health/live and /health/ready, then logs in as admin, reads /api/users/me/permissions and /api/projects and checks the admin user, its Admin role and a non-empty project page; writes .qa-test-results/<label>/deployed/deployed-proof.json and publishes DEPLOYED_PROBE_STATUS (0 passed, 1 failed probes).",
        [
            new PipelineFunctionParameter("backend_url", Required: true, "Loopback backend URL, http://127.0.0.1:<port> or http://localhost:<port>."),
            new PipelineFunctionParameter("admin_password_file", Required: true, "File holding the admin password, relative to the workspace."),
            new PipelineFunctionParameter("label", Required: true, "Result label ([a-zA-Z0-9_-]), the directory under .qa-test-results.")
        ],
        ["DEPLOYED_PROBE_STATUS"]);

    /// <summary>Runs a browser test suite from an immutable image against a QA stack's front and back
    /// containers and grades it from its TRX. Port of the immutable-image path of
    /// the former <c>run-qa-e2e-suite.sh</c> script; its exit code 10 is the status 1 of
    /// <c>E2E_SUITE_STATUS</c>.</summary>
    public static readonly PipelineFunctionDescriptor RunQaE2eSuite = new(
        "run-qa-e2e-suite",
        PipelineFunctionAccess.Build,
        "Checks the image's revision label, waits for the front container on its network, runs dotnet test of the suite inside the image, copies its results to .qa-test-results/<label>/e2e, classifies the TRX, lists the skipped and failed tests and publishes E2E_SUITE_STATUS (0 clean, 1 findings).",
        [
            new PipelineFunctionParameter("label", Required: true, "Run label ([a-zA-Z0-9_-]), the directory under .qa-test-results and the container names."),
            new PipelineFunctionParameter("front", Required: true, "Front container of the QA stack; its non-backend network is the test network."),
            new PipelineFunctionParameter("back", Required: true, "Back container of the QA stack."),
            new PipelineFunctionParameter("admin_password_file", Required: true, "File holding the admin password, relative to the workspace."),
            new PipelineFunctionParameter("image", Required: true, "Immutable test image."),
            new PipelineFunctionParameter("expected_commit", Required: true, "Commit the image's org.opencontainers.image.revision label must name, 40 hexadecimal characters."),
            new PipelineFunctionParameter("test_project", Required: true, "Test project inside the image, relative to its working directory."),
            new PipelineFunctionParameter("preserve_database", Required: false, "true or false (default), handed to the suite as E2E_PRESERVE_DATABASE."),
            new PipelineFunctionParameter("previous_payload_only", Required: false, "true when a payload-only V-1 runs V's suite, false (default)."),
            new PipelineFunctionParameter("test_filter", Required: false, "Empty (default) or Category=RollbackRelevant."),
            new PipelineFunctionParameter("workers", Required: false, "NUnit workers, 1 to 4. Defaults to AETHEUS_E2E_WORKERS, else 1.")
        ],
        ["E2E_SUITE_STATUS"]);

    /// <summary>Rebases the source roots of the restored Cobertura reports onto this workspace's
    /// <c>src</c> directory, in copies. Port of
    /// the former <c>rebase-quality-coverage-sources.mjs</c> script; the report list it printed is the
    /// <c>COVERAGE_REPORTS</c> output.</summary>
    public static readonly PipelineFunctionDescriptor RebaseCoverageSources = new(
        "rebase-coverage-sources",
        PipelineFunctionAccess.Build,
        "Copies every coverage/**/coverage.cobertura.xml to the output directory with each <source> below a src directory rebased onto the workspace's src, refuses any other source root, and publishes the copies separated by semicolons.",
        [new PipelineFunctionParameter("output", Required: true, "Directory the rebased copies are written to, relative to the workspace.")],
        ["COVERAGE_REPORTS"]);

    // PLAN-002 lot 1.10: the delivery tooling aetheus-ci called as scripts.

    /// <summary>Merges the per-project Roslyn SARIF reports of a build into one report holding only the
    /// open security findings. Port of the former <c>merge-roslyn-security-sarif.mjs</c> script.</summary>
    public static readonly PipelineFunctionDescriptor MergeRoslynSecuritySarif = new(
        "merge-roslyn-security-sarif",
        PipelineFunctionAccess.Build,
        "Reads every .sarif file of the input directory, keeps the results of the security rules (SEC*, CA21xx, CA23xx, CA3xxx, CA5xxx) that are not suppressed in source, and writes them as one SARIF 2.1.0 log; fails when the directory holds no report.",
        [
            new PipelineFunctionParameter("input", Required: true, "Directory of the per-project SARIF reports, relative to the workspace."),
            new PipelineFunctionParameter("output", Required: true, "Merged SARIF log to write, relative to the workspace.")
        ],
        []);

    /// <summary>Publishes, beside a gate status, the names of the tests that made it non-zero. Port of
    /// the former <c>publish-failed-tests.sh</c> script and of the publish mode of
    /// <c>list-failed-tests.mjs</c>: a diagnostic, never a verdict, so it never fails the step.</summary>
    public static readonly PipelineFunctionDescriptor PublishFailedTests = new(
        "publish-failed-tests",
        PipelineFunctionAccess.Build,
        "Reads the failed tests of the listed TRX files and failed-tests lists (an absent one is skipped) and, when any of the given statuses is not 0, publishes the first names that fit one line (+N more) under the named variable. Never fails the step.",
        [
            new PipelineFunctionParameter("variable", Required: true, "Run variable to publish, upper snake case, by convention <GATE>_FAILED_TESTS."),
            new PipelineFunctionParameter("sources", Required: true, "TRX files and failed-tests.txt lists, relative to the workspace, separated by spaces."),
            new PipelineFunctionParameter("statuses", Required: false, "Gate statuses the names explain, separated by spaces: nothing is published when every one is 0. Defaults to publishing whenever a failed test is found.")
        ],
        [])
    {
        NamedOutputParameter = "variable"
    };

    /// <summary>Writes the integration manifest the delivery contract seals: the source commit and the
    /// optional packages a consumer declares. Port of
    /// <c>write-integration-manifest.mjs</c> (the heredoc program of the .pipeline YAML).</summary>
    public static readonly PipelineFunctionDescriptor WriteIntegrationManifest = new(
        "write-integration-manifest",
        PipelineFunctionAccess.Build,
        "Writes {\"schema\":1,\"sourceCommit\":<commit>,\"packages\":<the packages array of the declaration>} as one line of JSON.",
        [
            new PipelineFunctionParameter("output", Required: true, "Manifest to write, relative to the workspace."),
            new PipelineFunctionParameter("declaration", Required: true, "Integration declaration holding a packages array, relative to the workspace."),
            new PipelineFunctionParameter("commit", Required: false, "Full 40-character source revision. Defaults to BUILD_SOURCEVERSION.")
        ],
        []);

    /// <summary>Seals the delivery contract of a candidate: its source, adapter, schema states,
    /// injected packages and artifact digests, with the candidate id derived from them. Port of
    /// <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/generate-delivery-contract.mjs</c>, whose environment inputs become parameters
    /// defaulting to the same variables.</summary>
    public static readonly PipelineFunctionDescriptor GenerateDeliveryContract = new(
        "generate-delivery-contract",
        PipelineFunctionAccess.Build,
        "Writes the schema 2 delivery contract (source, adapter, baseline and target schema fingerprints, enabled integration packages, SHA-256 of each artifact, candidate id and version) and publishes the candidate id and version.",
        [
            new PipelineFunctionParameter("manifest", Required: true, "Integration manifest, relative to the workspace."),
            new PipelineFunctionParameter("output", Required: true, "Contract to write, relative to the workspace."),
            new PipelineFunctionParameter("adapter_name", Required: true, "Delivery adapter name."),
            new PipelineFunctionParameter("adapter_version", Required: true, "Delivery adapter version."),
            new PipelineFunctionParameter("candidate_prefix", Required: true, "Candidate version, or its prefix when the version mode is not source."),
            new PipelineFunctionParameter("migrations_dir", Required: true, "Directory of the EF migration sources, relative to the workspace, or - for a delivery without a database."),
            new PipelineFunctionParameter("artifacts", Required: true, "Artifacts to seal, relative to the workspace, separated by spaces."),
            new PipelineFunctionParameter("source_sha", Required: false, "Full 40-character source revision. Defaults to BUILD_SOURCEVERSION."),
            new PipelineFunctionParameter("bootstrap", Required: false, "true when no deployed baseline exists. Defaults to DELIVERY_BASELINE_BOOTSTRAP, else true."),
            new PipelineFunctionParameter("version_mode", Required: false, "source uses the prefix as the version; anything else appends the first 12 characters of the candidate id. Defaults to DELIVERY_CANDIDATE_VERSION_MODE.")
        ],
        ["CANDIDATE_ID", "CANDIDATE_VERSION"]);

    /// <summary>Records the provenance of the artifacts a CI run produced: per category, the file
    /// count, total size and a digest over every file. Port of
    /// <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/generate-artifact-provenance.mjs</c>.</summary>
    public static readonly PipelineFunctionDescriptor GenerateArtifactProvenance = new(
        "generate-artifact-provenance",
        PipelineFunctionAccess.Build,
        "Writes the schema 1 artifact provenance: for each category, the file count, total bytes and a SHA-256 over every file's path, size and digest (symbolic links refused), plus the archives the agent release manifest declares.",
        [
            new PipelineFunctionParameter("output", Required: true, "Provenance file to write, relative to the workspace and inside it."),
            new PipelineFunctionParameter("commit", Required: false, "Full 40-character source revision. Defaults to BUILD_SOURCEVERSION."),
            new PipelineFunctionParameter("categories", Required: false, "JSON file mapping each category to workspace-relative paths, relative to the workspace. Defaults to AETHEUS_PROVENANCE_CATEGORIES, else the Aetheus layout.")
        ],
        []);

    /// <summary>Refuses a deployment whose deploy branch does not contain the release commit, after
    /// deepening a shallow checkout. Port of the former <c>verify-release-ancestry.sh</c> script, with the
    /// same Git commands and messages; publishes nothing.</summary>
    public static readonly PipelineFunctionDescriptor VerifyReleaseAncestry = new(
        "verify-release-ancestry",
        PipelineFunctionAccess.Build,
        "Reads the full release commit id from the provenance file, unshallows the checkout and fetches the commit from origin when missing (best effort, no credential prompt), then requires the deploy ref to contain it; an unknown commit or a still-shallow history is refused as unprovable.",
        [
            new PipelineFunctionParameter("source_commit_file", Required: true, "File holding the full (40 or 64 hexadecimal characters) release commit id, relative to the workspace, for instance .pipeline-artifacts/source-commit."),
            new PipelineFunctionParameter("deploy_ref", Required: false, "Ref that must contain the release commit. Defaults to HEAD.")
        ],
        []);

    /// <summary>Creates or verifies the independent evidence of a payload: the name, size and SHA-256 of
    /// each artifact for one source commit. Port of <c>nightly-evidence.mjs</c> (the heredoc program of the .pipeline YAML), writing
    /// the same file byte for byte, so the script's verify mode accepts what the function creates.</summary>
    public static readonly PipelineFunctionDescriptor PayloadEvidence = new(
        "payload-evidence",
        PipelineFunctionAccess.Build,
        "create writes {schema, kind, sourceSha, artifacts} (artifacts sorted by name, each with its bytes and SHA-256) as indented JSON; verify recomputes the artifacts and refuses evidence of another schema, kind or source commit, or whose artifacts differ. Artifact names must be unique and the files non-empty.",
        [
            new PipelineFunctionParameter("mode", Required: true, "create or verify."),
            new PipelineFunctionParameter("source_sha", Required: true, "Full 40-character hexadecimal source commit, recorded lowercase."),
            new PipelineFunctionParameter("evidence", Required: true, "Evidence file to write or read, relative to the workspace; its directory must exist."),
            new PipelineFunctionParameter("artifacts", Required: true, "Artifact files, relative to the workspace, separated by spaces; at least one."),
            new PipelineFunctionParameter("kind", Required: false, "Kind written and required in the evidence. Defaults to nightly-demo-payload.")
        ],
        []);

    /// <summary>Records the nightly qualification verdict: the security, quality, security history and
    /// extended dynamic security summaries, the assurance grade and a SHA-256 seal. Port of
    /// the former <c>record-nightly-qualification.mjs</c> script, writing the same document.</summary>
    public static readonly PipelineFunctionDescriptor RecordNightlyQualification = new(
        "record-nightly-qualification",
        PipelineFunctionAccess.Build,
        "Writes the schema 1 nightly qualification of a source commit: per summary its status, grade, completeness, run id and commit, the critical and high counts measured (null when not measured), the assurance grade, the generation time and a seal over the content. A summary of another commit is refused; a missing one is recorded as Unavailable.",
        [
            new PipelineFunctionParameter("output", Required: true, "Qualification file to write, relative to the workspace; its directory is created."),
            new PipelineFunctionParameter("source_sha", Required: true, "Full hexadecimal source commit (40 or 64 characters)."),
            new PipelineFunctionParameter("security", Required: true, "Directory holding exactly one security-summary.json at any depth, relative to the workspace."),
            new PipelineFunctionParameter("quality", Required: false, "Directory holding exactly one quality-summary.json, relative to the workspace."),
            new PipelineFunctionParameter("security_history", Required: false, "Directory holding exactly one security-summary.json of the security history analysis, relative to the workspace."),
            new PipelineFunctionParameter("extended_dynamic_security", Required: false, "Directory holding exactly one security-summary.json of the extended dynamic analysis, relative to the workspace."),
            new PipelineFunctionParameter("assurance_grade", Required: false, "Assurance grade A to F; anything else records no grade. Defaults to CANDIDATE_ASSURANCE_GRADE.")
        ],
        []);

    /// <summary>Tells which recent commit of an internal branch a public checkout is the distribution of.
    /// Port of the <c>--verify</c> mode of <c>scripts/export-public-distribution.mjs</c>. Unlike the
    /// script, which exited 1, the function succeeds when the public repository is refused: the caller
    /// branches on <c>PUBLIC_VERIFIED</c>.</summary>
    public static readonly PipelineFunctionDescriptor VerifyPublicDistribution = new(
        "verify-public-distribution",
        PipelineFunctionAccess.Build,
        "Compares the files of the public checkout's HEAD, byte for byte, with the distribution that each of the last depth commits of the branch made before its publication would export (the boundary.json of that commit, documentation left out per compareIgnore), and logs the verdict. Succeeds even when no commit matches: PUBLIC_VERIFIED is then false and PUBLIC_MATCHED_COMMIT empty. Fails only for an unusable input (not a git checkout, bad depth, Git error, no commit before the publication).",
        [
            new PipelineFunctionParameter("public_checkout", Required: true, "Git checkout of the public repository, relative to the workspace; the workspace is the internal checkout."),
            new PipelineFunctionParameter("branch", Required: false, "Internal branch or ref whose commits are the candidates, for instance HEAD. Defaults to main."),
            new PipelineFunctionParameter("depth", Required: false, "How many commits before the publication are tried, a positive integer. Defaults to 20.")
        ],
        ["PUBLIC_VERIFIED", "PUBLIC_MATCHED_COMMIT"]);

    /// <summary>All functions, in declaration order.</summary>
    public static IReadOnlyList<PipelineFunctionDescriptor> All { get; } =
    [
        DetectProjectLanguages, EnsureDotnetSdk, RestoreJavaScriptAnalysisDependencies, RunAffectedDotnetFormat,
        AppVersion, DeployIdentity, VerifyImmutableSource, PrepareImageInputs,
        EnsureTlsCertificates, GrantAcmeChallengeTraversal, BlueGreenHostReset, RetainDockerImages,
        PruneQaBuildxCache, PruneStaleQaCompose,
        // Qualification N/N-1 (PLAN-002 lot 1.5).
        RecordStorageEvidence, PersistCompatibilityMode,
        // PLAN-002 lot 1.6
        AuthorizeBaselineBootstrap, ExportBaselineIdentity, GenerateAssuranceContract, VerifyAssuranceContract,
        VerifyDeliveryPromotion, GeneratePackageSbom,
        // PLAN-002 lot 1.8, option c
        VerifyIdleFrontend, SettleFastReleaseTransaction, VerifyProductionState, VerifyProductionSecrets,
        // The heredoc programs of the production, nightly and demo preparations and of the vitrine publication
        BackfillEnvSecret, ReconcileEnvUrls, RenderEnvSample, RenderApacheUpstream, PublishVitrine,
        // PLAN-002 lot 8
        OpenGrepRuleTests,
        // PLAN-002 lot 1.10
        EnsureNodeRuntime, QaStackIdentity, ExtractAgentRelease, VerifySchemaContract,
        VerifyNMinusOneAgentContract, RunQaIntegrationSuite, RunQaDeployedProbes, RunQaE2eSuite,
        RebaseCoverageSources, MergeRoslynSecuritySarif, PublishFailedTests, WriteIntegrationManifest,
        GenerateDeliveryContract, GenerateArtifactProvenance, VerifyReleaseAncestry, PayloadEvidence,
        RecordNightlyQualification, VerifyPublicDistribution
    ];

    /// <summary>The function with that exact name, or <c>null</c>. Names are case-sensitive: the YAML
    /// spelling is the contract.</summary>
    public static PipelineFunctionDescriptor? Find(string? name) =>
        string.IsNullOrEmpty(name)
            ? null
            : All.FirstOrDefault(function => string.Equals(function.Name, name, StringComparison.Ordinal));

    /// <summary>Whether <paramref name="target"/> names a catalog function dispatchable as
    /// <paramref name="kind"/>. A deployment function sent as a build task is refused, so the access
    /// level cannot be downgraded by forging the task kind.</summary>
    public static bool IsValidTarget(OperationKind kind, string? target) =>
        Find(target) is { } function && function.Operation == kind;
}
