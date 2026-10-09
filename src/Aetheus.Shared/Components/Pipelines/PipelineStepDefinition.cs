// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// One step inside a pipeline stage. Most fields belong to a single <c>type:</c>, so the record is
/// deliberately wide: the YAML surface of every native step type is declared in one place, and each
/// field documents the type that owns it.
/// </summary>
public sealed record PipelineStepDefinition
{
    public string Name { get; init; } = string.Empty;
    public bool Remove { get; init; }
    public string Shell { get; init; } = string.Empty;
    public string? Type { get; init; }
    public string? Condition { get; init; }
    public bool Checkout { get; init; }
    public string? WorkingDirectory { get; init; }
    public int TimeoutSeconds { get; init; } = 300;
    public int RetryCount { get; init; }
    public bool ContinueOnError { get; init; }
    public string? Version { get; init; }
    public bool Changelog { get; init; }
    /// <summary>Marks a release created after a verified production go-live as the project's active
    /// deployment. The backend demotes the previously active release atomically.</summary>
    public bool Deployed { get; init; }
    /// <summary>Recette R2-001: for a <c>type: advance-branch</c> step, the branch the backend
    /// fast-forwards onto the commit of the release this run deploys (the release named by
    /// <c>AETHEUS_CANDIDATE_VERSION</c>, as its candidate recorded it). The backend hosts the
    /// repository and writes the ref itself, so the agent needs no git credential.</summary>
    public string? Branch { get; init; }

    /// <summary>Decision of 2026-10-07: for a <c>type: advance-branch</c> step, an optional tag the
    /// backend creates on the same deployed commit, named after the deployed version
    /// (<c>v$(APP_VERSION)</c>). A tag already on that commit is accepted; one on another commit fails
    /// the step, because a version names exactly one commit.</summary>
    public string? Tag { get; init; }
    public List<string> TargetFiles { get; init; } = [];
    /// <summary>PLAN-003 2.2 (4.5): for a <c>type: artifacts</c> step, the name the files matched by
    /// <see cref="TargetFiles"/> are stored and restored under. Several such steps in one stage publish
    /// several artifacts, where each used to need a stage of its own. Maps to YAML <c>artifact_name</c>.</summary>
    public string? ArtifactName { get; init; }
    /// <summary>PLAN-003 2.4: the category a <c>type: lint</c> step publishes its SARIF under,
    /// <c>code-quality</c> (default) or <c>accessibility</c>. Maps to YAML <c>analysis_category</c>.</summary>
    public string? AnalysisCategory { get; init; }
    /// <summary>Run variables this step publishes at run time from a script (a
    /// <c>##aetheus[setvariable]</c> line the step text does not show). Declaring them is what lets a
    /// <c>variables:</c> value reference one: the launch check accepts a name some step will provide,
    /// and the value is expanded again once that step has succeeded. Upper snake case only.</summary>
    public List<string> Outputs { get; init; } = [];
    /// <summary>Manifest key for a <c>type: scanner</c> step. The backend accepts only keys from the
    /// embedded immutable scanner manifest; pipeline YAML cannot supply an image or command.</summary>
    public string? Scanner { get; init; }
    /// <summary>Logical scope for a <c>type: analysis-gate</c> step. It only labels the immutable
    /// machine and human summaries; the backend computes the verdict from every report in the run.</summary>
    public string? AnalysisScope { get; init; }
    public string? AnalysisPreset { get; init; }
    public List<PipelineAnalysisGateRuleDefinition> AnalysisRules { get; init; } = [];
    public PipelineAnalysisGradingDefinition? AnalysisGrading { get; init; }
    /// <summary>Inline prompt for a provider-agnostic <c>type: ai</c> step.</summary>
    public string? Prompt { get; init; }

    /// <summary>Workspace-relative prompt file for a <c>type: ai</c> step.</summary>
    public string? PromptFile { get; init; }

    /// <summary>Name of the organization-scoped AI runner profile.</summary>
    public string? Profile { get; init; }

    /// <summary>When true, only an explicit <c>VERDICT: PASS</c> makes the step succeed.</summary>
    public bool Gate { get; init; }

    /// <summary>Workspace-relative paths explicitly highlighted to the AI runner.</summary>
    public List<string> ContextPaths { get; init; } = [];

    /// <summary>DAST target for a scanner that requires one. It must be an absolute HTTP(S) URL and is
    /// accepted only together with a non-production target classification.</summary>
    public string? TargetUrl { get; init; }

    /// <summary>Classification used by the backend and agent DAST guards: ephemeral, qa, pre-release,
    /// shared, production or real-data.</summary>
    public string? TargetClassification { get; init; }

    /// <summary>Validated OpenAPI or GraphQL definition URL for <c>zap-api</c>. Its hostname must be
    /// the exact allowlisted hostname of the trusted non-production environment.</summary>
    public string? ApiSpecificationUrl { get; init; }

    /// <summary>Recette R2-099: several definition URLs for one <c>zap-api</c> step, in place of
    /// <see cref="ApiSpecificationUrl"/>. The agent scans them one after the other, each bounded to
    /// <see cref="TimeoutSeconds"/> divided by their number, and publishes one merged report.</summary>
    public List<string> ApiSpecificationUrls { get; init; } = [];

    /// <summary>ZAP API definition format: <c>openapi</c> or <c>graphql</c>.</summary>
    public string? ApiSpecificationFormat { get; init; }

    /// <summary>Enables active DAST. Active scans are rejected unless the target is explicitly
    /// classified as ephemeral, QA or pre-release and is not shared or real-data.</summary>
    public bool Active { get; init; }

    /// <summary>type: apache-config - map of destination vhost filename to its versioned template
    /// below <c>.pipeline/configs/</c>. The whole set is rendered and applied atomically.</summary>
    public Dictionary<string, string> ConfigFiles { get; init; } = [];

    /// <summary>S-FEAT-K3P8: minimum line-coverage percent (0–100) for a <c>type: coverage</c> step.
    /// When set, the step fails if the published line rate is below it (respecting continue_on_error).</summary>
    public double? MinCoverage { get; init; }

    /// <summary>Producer name persisted with a generic coverage report (for example Coverlet,
    /// Istanbul, coverage.py or JaCoCo). The value is metadata only and never selects a command.</summary>
    public string? CoverageTool { get; init; }

    /// <summary>Language associated with a generic coverage report.</summary>
    public string? CoverageLanguage { get; init; }

    /// <summary>Version of the project-owned coverage producer, when known.</summary>
    public string? CoverageVersion { get; init; }

    /// <summary>S-FEAT-D7M5: maximum allowed cyclomatic complexity for a <c>type: complexity</c> step.
    /// When set, the step fails if the highest method CC exceeds it (respecting continue_on_error).</summary>
    public int? MaxComplexity { get; init; }

    // ── Typed .NET test run (type: dotnet-test) ────────────────────────────────────────────────────
    // PLAN-006 lot 11.3. The command is the trivial part; what this step exists for is classifying its
    // result, so a suite that ran and failed is published as a finding while a suite whose evidence
    // cannot be trusted fails the run outright. Every project that wrote that in shell got it wrong.

    /// <summary>The test project, relative to the workspace. Required for <c>type: dotnet-test</c>.
    /// Named <c>test_project</c> rather than <c>project</c> because <c>project</c> already means the
    /// Compose project on a bluegreen step, and one YAML key meaning two things is how a step lands
    /// on the wrong target.</summary>
    public string? TestProject { get; init; }

    /// <summary>Where the TRX and any coverage land, relative to the workspace.</summary>
    public string? ResultsDirectory { get; init; }

    /// <summary>The run variable the classified status (0 clean, 1 findings) is published under, which
    /// is what the stage's gate then reads.</summary>
    public string? StatusVariable { get; init; }

    /// <summary>The TRX file name inside <see cref="ResultsDirectory"/>. Defaults to results.trx.</summary>
    public string? TrxName { get; init; }

    /// <summary>Collect Cobertura coverage and refuse the run when none is produced. A coverage gate
    /// fed by a run that collected nothing reports a clean score for a measurement nobody made.</summary>
    public bool CollectCoverage { get; init; }

    /// <summary>A runsettings file, relative to the workspace.</summary>
    public string? RunSettings { get; init; }

    /// <summary>Build configuration for the test run. Defaults to Release.</summary>
    public string? Configuration { get; init; }

    // ── Aggregated gate (type: gate-status) ───────────────────────────────────────────────────────
    // PLAN-006 lot 11.3. Typed for one reason: a status nobody published must read as a failure. In
    // shell the default falls the other way, and a gate that passes because its input never arrived
    // is the worst way for a gate to fail.

    /// <summary>The run variables to aggregate. Required for <c>type: gate-status</c>.</summary>
    public List<string> StatusVariables { get; init; } = [];

    /// <summary>The run variable the aggregated verdict is published under.</summary>
    public string? PublishStatusAs { get; init; }

    /// <summary>Fail the step when the verdict is non-zero. An advisory gate publishes its verdict
    /// and leaves the decision to whoever reads it.</summary>
    public bool Blocking { get; init; }

    /// <summary>What the gate is about, used in its log lines.</summary>
    public string? GateLabel { get; init; }

    // ── Cross-agent deploy (type: deploy) ──────────────────────────────────────────────────────────
    // The four fields below configure a deployment step. The backend resolves which artifact to ship
    // from Artifact (same-run) OR Release (an existing release), infers DeployKind from Compose, and
    // dispatches an OperationKind.PipelineDeploy to a deployment-capable agent. App is the application
    // instance name (validated against OperationTargetValidator.DeployAppRegex at dispatch).

    /// <summary>Same-run artifact name to deploy (produced by an earlier <c>type: artifacts</c> step in
    /// this run). Mutually informative with <see cref="Release"/>: when both are null the backend fails
    /// the step with a clear "nothing to deploy" error.</summary>
    public string? Artifact { get; init; }

    /// <summary>Relative destination used by <c>type: restore-artifacts</c>. The control plane and
    /// agent both reject rooted paths and traversal segments. Null restores into the workspace root.</summary>
    public string? TargetDirectory { get; init; }

    /// <summary>Bootstrap-only escape hatch for <c>type: restore-artifacts</c> with the literal
    /// <c>release: latest-published</c>, <c>release: previous-published</c>, or
    /// <c>release: previous-deployed</c>, or <c>release: current-deployed</c>. When true and no retained
    /// rollback-capable artifact exists for that selector, the
    /// step succeeds explicitly without dispatching an agent task so later steps can prove a first
    /// contract-release bootstrap. Imported/tag-only release metadata does not disable this bootstrap.
    /// It never suppresses an invalid selector or a failed artifact download.</summary>
    public bool AllowMissing { get; init; }

    /// <summary>Existing release to deploy (scenario 3): a release id, a version string, or the literal
    /// <c>"latest"</c> to resolve the newest release of the run's project, or
    /// <c>"latest-published"</c> to resolve the newest retained publishable/deployed release, or
    /// <c>"previous-published"</c> to resolve the newest published one whose artifact commit differs from
    /// the current run, or <c>"previous-deployed"</c> to resolve the active deployed predecessor at a
    /// different commit, or <c>"current-deployed"</c> to resolve the factually deployed release even
    /// when it shares the current source commit. Takes the
    /// artifact from that release instead of the current run. Also supported by
    /// <c>type: restore-artifacts</c> for an exact retained N-1 payload.</summary>
    public string? Release { get; init; }

    /// <summary>Application instance name on the target agent. Doubles as the systemd template instance
    /// (<c>aetheus-app@&lt;app&gt;</c>) and the on-disk deploy directory, so it is validated against the
    /// strict <c>^[a-zA-Z0-9_-]{1,64}$</c> shape (<see cref="OperationTargetValidator.DeployAppRegex"/>)
    /// before dispatch and re-validated by the root-owned restart helper agent-side.</summary>
    public string? App { get; init; }

    /// <summary>Path (within the artifact) to a docker compose file. When set, the deploy runs in
    /// container mode (<c>docker load</c> + <c>docker compose up -d --wait</c>); when null, binary mode
    /// (atomic symlink flip + systemd restart helper). This is how <c>DeployKind</c> is inferred.</summary>
    public string? Compose { get; init; }

    /// <summary>Dedicated post-deploy health-gate stabilization window (seconds) for a binary
    /// <c>type: deploy</c> step. When &gt; 0 the agent uses it verbatim (clamped to a 20s floor and the
    /// overall step timeout) instead of deriving the window from <see cref="TimeoutSeconds"/>/3 - so a
    /// legitimately slow .NET cold start (JIT + EF migrations) is not mistaken for a crash loop and
    /// rolled back. 0 (default) keeps the timeout-derived window. Maps to YAML <c>health_timeout_seconds</c>.</summary>
    public int HealthTimeoutSeconds { get; init; }

    /// <summary>Optional functional readiness URL for a binary <c>type: deploy</c> step. It must be
    /// an absolute loopback HTTP(S) URL. After systemd is stable the target agent requires a 2xx
    /// response before declaring success; failure follows the same automatic rollback path. Maps to
    /// YAML <c>health_url</c>.</summary>
    public string? HealthUrl { get; init; }

    /// <summary>Backup-run selector for <c>type: restore-backup</c>. Manual rollbacks pass the
    /// verified run through <c>$(AETHEUS_ROLLBACK_BACKUP_RUN_ID)</c>; the backend resolves it rather
    /// than trusting a file path supplied by YAML.</summary>
    public string? BackupRun { get; init; }

    // ── Host Apache reverse proxy (type: apache-proxy) ───────────────────────────────────────────────
    // The backend renders a versioned reverse-proxy template from .pipeline/configs/ at the exact
    // run commit, then dispatches an OperationKind.ApacheConfigureProxy to an Apache-manage-capable
    // agent that writes the vhost, enables the site and gracefully reloads Apache on the host.

    /// <summary>type: apache-proxy - optional versioned template path below
    /// <c>.pipeline/configs/</c>. Defaults to
    /// <c>.pipeline/configs/apache/reverse-proxy-http.conf</c>. Maps to YAML
    /// <c>config_template</c>.</summary>
    public string? ConfigTemplate { get; init; }

    /// <summary>type: apache-proxy - the <c>ServerName</c> for the rendered reverse-proxy vhost
    /// (the public host/domain). Maps to YAML <c>server_name</c>.</summary>
    public string? ServerName { get; init; }

    /// <summary>type: apache-proxy - the upstream the vhost proxies to, e.g.
    /// <c>http://127.0.0.1:8090</c> (the published port of the deployed container). Maps to <c>upstream</c>.</summary>
    public string? Upstream { get; init; }

    // ── Host HTTPS via Certbot (type: certbot) ───────────────────────────────────────────────────────

    /// <summary>type: certbot - comma-separated domain(s) to obtain a certificate for. Maps to
    /// <c>domains</c>. On a host that cannot complete ACME validation (no public DNS / unreachable
    /// port 80) the agent falls back to a self-signed certificate in the Let's Encrypt layout so the
    /// site still serves HTTPS ("do the closest thing").</summary>
    public string? Domains { get; init; }

    /// <summary>type: certbot - contact email for the ACME account / expiry notices. Maps to <c>email</c>.</summary>
    public string? Email { get; init; }

    // ── Deployment evidence (type: smoke) ───────────────────────────────────────────────────────────

    /// <summary>type: smoke - the http(s) origin to probe, with no path or query. Every probe path is
    /// composed by the agent, so a step cannot repoint a probe at an arbitrary endpoint. Maps to
    /// <c>origin</c>.</summary>
    public string? Origin { get; init; }

    /// <summary>type: smoke - the http(s) origin the deployed API answers on, when it is not the same
    /// as <see cref="Origin"/>. Empty means same-origin, which is what every consumer assumed until a
    /// deployment with a separate API host proved otherwise: the browser suite authenticates against
    /// this origin, and pointing it at the application host made the login land on a static file
    /// server that answers 405 to a POST. Same shape as <see cref="Origin"/> - an origin, never a
    /// path - so a step still cannot repoint a probe at an arbitrary endpoint. Maps to
    /// <c>api_origin</c>.</summary>
    public string? ApiOrigin { get; init; }

    /// <summary>type: smoke - number of readiness samples to take (1-50, default 1). A string like
    /// <see cref="MaxSeconds"/> so a pipeline can supply it through a <c>$(VARIABLE)</c>, which the
    /// control plane only substitutes into string fields. Maps to <c>samples</c>.</summary>
    public string? Samples { get; init; }

    /// <summary>type: smoke - response-time budget in seconds for the slowest readiness sample.
    /// Exceeding it is a finding, never a step failure: a cold cache is enough to blow the budget and
    /// that must not be able to undo a deployment that answers correctly. Maps to
    /// <c>max_seconds</c>.</summary>
    public string? MaxSeconds { get; init; }

    /// <summary>type: smoke - also assert the frontend contract production enforces before a cutover:
    /// a real HTML shell served <c>no-store</c>. Maps to <c>frontend</c>.</summary>
    public bool Frontend { get; init; }

    /// <summary>type: smoke - immutable image carrying the authenticated browser suite. Empty skips
    /// the suite. An image that cannot run fails the step (no evidence), a suite that runs and fails
    /// is a finding. Maps to <c>browser_image</c>.</summary>
    public string? BrowserImage { get; init; }

    /// <summary>type: smoke - test filter for the browser suite. Maps to <c>filter</c>.</summary>
    public string? Filter { get; init; }

    /// <summary>
    /// type: smoke - what a finding costs the step. <c>advisory</c> (the default) records findings and
    /// still exits 0; <c>blocking</c> fails the step on a blocking finding, which is what lets a
    /// deployment stage compensate instead of merely annotating a bad cutover. The response-time
    /// budget is advisory in both modes. Maps to <c>evidence_gate</c>; the shorter <c>gate</c> is
    /// already the boolean verdict switch of <c>type: ai</c>.
    /// </summary>
    public string? EvidenceGate { get; init; }

    // ── Blue-green host deployment (type: bluegreen-*) ──────────────────────────────────────────────
    // Shared by bluegreen-migrate / -up / -switch / -commit. The four steps re-derive this same
    // environment independently, which is what lets them run as separate steps rather than one
    // opaque block holding a process-scoped lock.

    /// <summary>type: bluegreen-* - Compose project the operation is scoped to. Also the operation
    /// target, so the agent refuses a scope it cannot validate before touching anything. Maps to
    /// <c>project</c>.</summary>
    public string? Project { get; init; }

    /// <summary>type: bluegreen-* - absolute directory holding the live colour, the transaction
    /// journal and the deployed revision. Must live outside any tree an installer purges. Maps to
    /// <c>state_dir</c>.</summary>
    public string? StateDir { get; init; }

    /// <summary>type: bluegreen-* - environment file supplying the Compose variables. Maps to
    /// <c>env_file</c>.</summary>
    public string? EnvFile { get; init; }

    /// <summary>type: bluegreen-* - comma-separated Compose files, base first. Maps to
    /// <c>compose_files</c>.</summary>
    public string? ComposeFiles { get; init; }

    /// <summary>type: bluegreen-* - the four distinct host ports, as
    /// <c>frontBlue,backBlue,frontGreen,backGreen</c>. Maps to <c>ports</c>.</summary>
    public string? Ports { get; init; }

    /// <summary>type: bluegreen-switch - revision recorded in the journal and, on commit, as the
    /// deployed revision. Maps to <c>revision</c>.</summary>
    public string? Revision { get; init; }

    /// <summary>type: bluegreen-switch - absolute path of the root-owned helper that applies and
    /// validates the switch. Invoked argv-exact through sudo; a rejected configuration must fail the
    /// reload while the previous colour keeps serving. Maps to <c>reload_helper</c>.</summary>
    public string? ReloadHelper { get; init; }

    /// <summary>type: bluegreen-switch - template rendered to point the web server at the chosen
    /// colour. Supports <c>#{FRONT_PORT}#</c>, <c>#{BACK_PORT}#</c> and <c>#{COLOR}#</c>. Rendering
    /// this file IS the switch: reloading without rewriting it would reload the configuration that
    /// is already live. Maps to <c>upstream_template</c>.</summary>
    public string? UpstreamTemplate { get; init; }

    /// <summary>type: bluegreen-switch / -rollback - destination the rendered template is written to,
    /// and the file restored from the journal snapshot when a switch is undone. Maps to
    /// <c>upstream_conf</c>.</summary>
    public string? UpstreamConf { get; init; }

    /// <summary>type: bluegreen-migrate - directory holding the EF migration sources, so the
    /// expand/contract gate can inspect the pending ones before they run against a schema the
    /// previous colour is still serving. Maps to <c>migrations_dir</c>.</summary>
    public string? MigrationsDir { get; init; }

    /// <summary>type: bluegreen-switch - PLAN-003 2.7: minutes the host gives the deployment to be
    /// committed before it puts traffic back on the previous colour by itself. Empty or 0 arms
    /// nothing. Maps to <c>confirm_minutes</c>.</summary>
    public string? ConfirmMinutes { get; init; }

    /// <summary>type: bluegreen-* - comma-separated names of run variables to expose to the
    /// <c>docker compose</c> process, so a Compose file can interpolate per-run values such as the
    /// image tags built by CI. A shell cutover exported these itself; a typed step cannot, and the
    /// agent inherits only its own service environment, so without this the Compose defaults would
    /// silently apply. The step names exactly what it needs: every other run variable, including
    /// vault secrets unrelated to Compose, stays out of the child process. A name that resolves to
    /// nothing is refused before the task is created. Maps to <c>compose_env</c>.</summary>
    public string? ComposeEnv { get; init; }

    // ── Pipeline orchestration (type: trigger) ──────────────────────────────────────────────────────

    /// <summary>type: trigger - the name of another pipeline (same project) to trigger and wait for.
    /// The step stays Running until the triggered child run reaches a terminal status, then mirrors it
    /// (child Success ⇒ step Success; child Failed/Cancelled ⇒ step Failed, respecting
    /// <see cref="ContinueOnError"/>). Maps to YAML <c>pipeline</c>.</summary>
    public string? Pipeline { get; init; }

    /// <summary>type: trigger - extra non-secret variables passed to the child run. System lineage
    /// variables are applied afterwards and cannot be overridden. Maps to YAML <c>variables</c>.</summary>
    public Dictionary<string, string> Variables { get; init; } = [];

    /// <summary>type: trigger - queue-time parameters passed to the child pipeline. Values are
    /// substituted from the parent run before the child validates them against its declared
    /// <c>parameters:</c> contract. Maps to YAML <c>parameters</c>.</summary>
    public Dictionary<string, string> Parameters { get; init; } = [];

    /// <summary>type: trigger - preserve the parent's exact source commit and branch in the child.
    /// This is the safe default for ordinary build/validate/deploy orchestration. A conformance
    /// pipeline may set it to false to launch a deliberately independent baseline revision.</summary>
    public bool InheritSource { get; init; } = true;

    /// <summary>type: trigger - branch to resolve when <see cref="InheritSource"/> is false. This is
    /// intended for explicit cross-version compatibility laboratories; it never accepts a moving
    /// branch while source inheritance remains enabled.</summary>
    public string? SourceBranch { get; init; }

    /// <summary>type: trigger - optional immutable commit to resolve on <see cref="SourceBranch"/>
    /// when <see cref="InheritSource"/> is false. The value is substituted from the parent run and
    /// must resolve to a full 40- or 64-character hexadecimal commit hash. Maps to YAML
    /// <c>source_commit</c>.</summary>
    public string? SourceCommit { get; init; }

    /// <summary>type: restore-artifacts - name of the upstream pipeline, triggered by the direct
    /// parent orchestration run, that produced <see cref="Artifact"/>. Maps to
    /// YAML <c>artifact_source_pipeline</c>.</summary>
    public string? ArtifactSourcePipeline { get; init; }

    /// <summary>type: restore-artifacts - how <see cref="ArtifactSourcePipeline"/> is resolved when
    /// the current run did not itself trigger that pipeline. Maps to YAML
    /// <c>artifact_source_selector</c>.
    ///
    /// <c>same-commit</c> (the default, and the only behaviour before it existed) requires the
    /// producing run to stand on the exact commit of the consuming run. That is what provenance
    /// needs when the artifact IS the thing being deployed.
    ///
    /// <c>latest-successful</c> takes the newest successful run of that pipeline in the project,
    /// whatever its commit. It exists for evidence a scheduled pipeline produces about a DIFFERENT
    /// commit - a nightly qualification read by a deployment - where requiring the same commit means
    /// never resolving anything at all. The consumer is then responsible for judging whether that
    /// evidence describes it, which is a question about commit ancestry and age that the artifact
    /// store cannot answer.</summary>
    public string? ArtifactSourceSelector { get; init; }
}
