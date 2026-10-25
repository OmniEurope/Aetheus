<!-- SPDX-License-Identifier: EUPL-1.2 -->
# Pipelines

YAML-defined CI/CD pipelines: definition, validation, runs, stages, jobs, steps, scheduling, approvals, artifacts, test results, templates, webhooks, and dry-run/preflight checks. Supports both legacy `stages > steps` format and Azure DevOps-style `stages > jobs > steps` format (flattened via `YamlParsingHelper.FlattenJobs()`).

Stages and jobs accept an `os:` key (`linux` / `windows`, inherited stage → job) that pins resolution to a runner whose `Server.OsType` matches (`OsType` enum + `OsTypeHelper` in `Aetheus.Shared`); an OS-constrained stage with no compatible online runner fails the run with a clear reason.

Run, stages, and jobs also accept an `isolation:` block (`PipelineIsolationDefinition`: `mode` = `process` (default) / `container`, plus `image` / `runtime` / `network`, inherited run → stage → job). `mode: container` runs ordinary and typed AI steps inside an ephemeral, hardened container on the agent (`ContainerRunExecutor`, `docker run --rm --cap-drop ALL --security-opt no-new-privileges --pids-limit`; pluggable runtime `runc`/`runsc`/`kata`). Container-isolated stages resolve **only** to `Server.DockerAvailable` runners and **fail-closed** when none is available. See ADR-017 and ADR-023.

**Step types.** A step's `type:` picks its dispatch path (default is a free-form `shell` step): `substitute` (`#{VAR}#` token replacement in `target_files`), `release` (changelog + `Release` creation), `coverage` (publish Cobertura XML), `lint` (publish SARIF 2.1.0), `complexity` (publish Roslyn cyclomatic-complexity, LOC and CRAP metrics), `deploy`, `apache-proxy`, `apache-config`, `certbot`, `trigger`, `artifacts`, `restore-artifacts`, `restore-backup`, `publish-observability`, `scanner`, `analysis-gate`, `ai`, `smoke`, `dotnet-test`, `gate-status`, `mutation`, `advance-branch`, and the seven blue-green types
(`bluegreen-migrate`, `bluegreen-up`, `bluegreen-switch`, `bluegreen-commit`, `bluegreen-rollback`, `bluegreen-retire`, `bluegreen-revert`); the list is `PipelineDefinitionValidator.KnownTypedStepTypes`. The `publish-observability` step dispatches the agent-owned trusted harness that signs, preflights, publishes and reads back the immutable bundle from the Aetheus registries. The `ai` step resolves an `AiRunnerProfile`, is claimed through the published `ai.run` capability, publishes a Markdown report and optional patch, and applies the deterministic `VERDICT:` gate when requested. Apache configuration is rendered from versioned `.pipeline/configs/**` templates and applied transactionally. Scanner steps dispatch only immutable manifest entries and publish normalized security/quality reports; `analysis-gate` reads the aggregated run verdict. Restore steps accept a successful child artifact or a retained same-project release and fail closed when provenance or the rollback contract is invalid. Coverage is also auto-detected during artifact collection, so an explicit `type: coverage` step is optional. See ADR-012 for the full YAML model, ADR-023 for AI semantics, ADR-030 for scanner/gate semantics, and ADR-037 for the package-registry destination.

**Pipeline-as-orchestrator (`type: trigger`).** A `trigger` step launches another pipeline in the same project and **waits** for it to finish before the parent stage advances. Its `variables:` map supplies ordinary child variables, while `parameters:` supplies and validates the child's declared queue-time parameters after parent-variable substitution. The dispatch half (`PipelineTriggerStepCoordinator.CreateTriggerStepAsync`) links the child run to the parent step via `PipelineStepRun.TriggeredRunId`; the wait half (`PipelineRunCompletedTriggerHandler`, an `IDomainEventHandler<PipelineRunCompletedEvent>`) mirrors the child's terminal status onto the step (child Success ⇒ step Success; anything else ⇒ Failed, honouring `continue_on_error`) and re-enters `AdvanceStageAsync`. Cancelling an active orchestration run walks those persisted links and cancels every active descendant recursively, including pending runs and grandchildren. This lets one pipeline orchestrate others, par exemple `toto-conformance` pour préparer une baseline indépendante, qualifier V puis promouvoir exactement la candidate. Les anciens exemples `toto-orchestration-accept` appartiennent au projet `Toto Demo` et restent uniquement sous les noms `toto-legacy-*`. A **pure orchestration** pipeline - every step is `type: trigger` - needs no workspace, so `PipelineRunPreparationService.RequiresWorkspace` skips injecting `System:Prepare`/`System:Cleanup` for it (no "Clone Repository"/"Cleanup" rows); the child run view renders each trigger step as a collapsible node that lazy-loads the child's own timeline (front `RunTimelineTree`). A background self-heal (`PipelineTriggerReconcileService`) recovers a trigger step whose child completion event was lost, re-drives a run that has pending steps but no in-flight work after `SchedulerRecoveryGrace`, and force-fails a terminal dead end past `RunStuckTimeout` (see `BackgroundServicesOptions`).

**Branch advance after a deployment (`type: advance-branch`, recette R2-001, ADR-044 amendment).** Run by the backend like `trigger`, with no agent task (`PipelineBranchAdvanceStep`, through Git's `IGitBranchAdvanceService`): the branch named by `branch` (required; a literal is validated, a variable-built one is checked at run time) is fast-forwarded onto the commit recorded by the release `AETHEUS_CANDIDATE_VERSION` of the pipeline's project. That release must already be `Deployed`, so the step goes after the `type: release` step with `deployed: true`; project-owned pipelines only. `Advanced` (audited `AdvancedBranchAfterDeploy`) and `AlreadyUpToDate` succeed; `NotFastForward`, `CommitNotFound`, a missing or undeployed release and a refusal fail the step with the reason. Every outcome is written to the run warnings.

**Forward-compatible definitions (recette R2-041).** A YAML key this backend does not know is skipped with a warning (`PipelineYamlDiagnostics.AppendUnknownPropertyWarnings`, editor validation and run), so a definition written for a newer backend still runs; shape errors still refuse. An unknown key one edit away from a key the same block knows (two edits for keys of six letters or more, keys of four letters or more only) is a typo and refuses the definition, at validation (`PipelineService`) and at run preparation (`PipelineRunPreparationService`, 400), so a misspelled guard is never silently dropped (`NearMissKeyErrors`). An unknown step `type:` is a warning; if such a step has no `shell` and no `checkout`, `PipelineStepTaskDispatcher` fails it by name (`ToolError`) when it runs.

**Versioned Apache configuration (`type: apache-config`).** A `config_files` map selects 1-32 templates under `.pipeline/configs/**` at the run's exact SHA. Strict `#{VAR}#` rendering feeds one encrypted `ApacheApplyConfigSet` (311); the agent snapshots the affected files and links, validates through its fixed helper, reloads once, and restores/reloads the prior set on failure.

**Git-strict project pipelines (ADR-015).** `Pipeline.SourceRepositoryId` selects the one Aetheus-hosted repo or enabled external mirror that supplies the authoritative `.pipeline/<name>.yaml`, clone URL and full immutable SHA. Existing mono-repository projects are backfilled; an unbound multi-repository project and any workspace run without a full SHA fail before run creation. The run snapshots YAML, branch, SHA and repository URL. UI/API edits against an external mirror currently create local mirror commits only; write-back to the external remote is not supported.

**Candidate, deployment, fast release, nightly and package publication roots.** A push on `develop` launches `aetheus-candidate`: CI creates the immutable application artifact, Quality and Security publish advisory grades while mechanical child failures remain blocking, then one disposable QA validates V, dynamic security and the V-1 binary rollback on schema V before an unconditional teardown. Test and performance outcomes converge on advisory gate boundaries, so they remain visible without blocking candidate creation. Candidate then seals those outputs and the three same-commit grade summaries into an immutable assurance contract and creates the non-deployed release `c-<SHA>`. The manual `aetheus-deploy-prod` root restores an explicitly selected Candidate, verifies its assurance contract fail-closed, deploys the same bytes and marks that release `Deployed` without rebuilding or retesting. The manual `aetheus-release-fast` emergency root instead builds and deploys the exact selected `develop` SHA autonomously, with no Candidate, prior release or child pipeline. `aetheus-nightly`, pinned to `develop` at 00:00 UTC, reruns the candidate with performance smoke and full-history Gitleaks; it never deploys production; it deploys a disposable QA (Compose project `aetheus-nightly`, state under `/var/lib/aetheus-nightly`, hosts `qa.*`) that its last stage, `Tear down QA` (`condition: always()`), empties with the `bluegreen-host-reset` function (recette R-523). The persistent demo (`aetheus-demo`, state under `/var/lib/aetheus-demo`) belongs to the `aetheus-demo` root since 2026-10-02: once a day it clones the public GitHub repository, proves with `export-public-distribution.mjs --verify` that it is the distribution of one of the last `main` commits, builds its images from those public sources, resets the demo and deploys it. It is the first pipeline to **extend `host-bluegreen-deploy@2`** rather than carry its own shell cutover: its inline `Prepare demo host` shell step (the same block as the nightly's `Prepare QA host`) establishes what the typed steps do not know about (the demo identity contract, TLS and the HTTPS vhost, the Vault-held secrets, the payload loaded as revision-tagged images, and the run variables Compose interpolates; the upstream file, the bootstrap identity key and the public URLs are the `render-apache-upstream`, `backfill-env-secret` and `reconcile-env-urls` functions, in stages between the two shell steps), the `bluegreen-*` and `smoke` steps own the cutover. The preparation accepts a first deploy with no existing colour or migration history, and holds no transaction, so a failure in it leaves the live demo untouched. `publish-observability-packages` is a distinct manual root: its three package children build and consume candidates, then the parent publishes one immutable bundle exclusively to the Aetheus registries (ADR-037). Legacy and demo definitions are not part of the active Aetheus catalog. See [`docs/self-deploy-guide.md`](../../../../docs/self-deploy-guide.md) for the operating contract.

**Verified checkpoint resume (ADR-036).** The explicit manual `ResumeCheckpoints` rerun pins the source
snapshot, commit and parameters. Only successful CI, Quality and Security children are eligible; QA,
readiness and assurance steps always replay. Reuse fails closed unless the definition, orchestrator,
agent and scanner contracts still match and every associated artifact passes its stored size/SHA-256
check under a retention lease. Any missing or divergent proof falls back to a normal child replay.

**Agent lifecycle boundary (ADR-035).** Candidate may package the passive `agent-release` artifact for distribution and compatibility evidence, but Candidate, Release, Promote and Deploy never bootstrap, request or execute an agent update. Updating installed agents remains an explicit fleet operation, independent from application delivery.

**Toto conformance projects.** `Seed:ConformanceToto=true` provisions the healthy `Toto` V-1/V delivery laboratory and the isolated expected-red `Toto Vulnerable` project. The latter has only CI, Quality, Security and negative conformance pipelines: CI must pass, both real gates must return `Blocked`, and no QA, candidate, release, deployment or promotion path exists. `Toto Demo` is created separately by `Seed:Demo` and is never conformance evidence.

**Dependency view.** `GET /api/pipelines/dependencies` returns the caller-visible pipelines split into parents and leaves. `PipelineDependencyGraphBuilder` derives a parent's references from `on_success` entries and `type: trigger` steps; the global pipelines page renders both groups as navigable grids.

**Pipeline owner and its transfer (recette R1-49, incident 2026-10-11).** `Pipeline.CreatedByUsername` is the principal the F-EXEC-1b gate (`PipelineTriggerStepCoordinator.ChainedRunAuthorizedAsync`) authorizes chained and `on_success` runs against: it must hold Server.Admin on every target server. An inactive or missing owner holds no role, so every chained run is refused (`BlockedUnauthorizedChainedRun` / `BlockedUnownedChainedRun`). The gate is unchanged; the owner is what changes. `PipelineOwnershipService` (`api/pipelines/ownership`, platform Admin only) lists a user's pipelines (`owned`), the active users a pipeline can be given to (`candidates`), previews a transfer (`preview`, nothing written) and applies it (`transfer`). The scope is exactly one of a pipeline, a project, or a current owner; the new owner must be an active user. Each item says whether the new owner holds Server.Admin on the pipeline's targets, resolved with the default parameters and checked with the gate's own `HasPermissionAsync(owner, Server, id, Admin)` call (false: chained runs would still be refused; null: the targets could not be resolved, with the reason). Each changed pipeline writes one `PipelineOwnerTransferred` audit entry (`name: owner 'old' -> 'new' by 'actor'`). `PipelineDto` and `PipelineDependencyDto` carry `OwnerUsername` and `OwnerInactive`, shown as a warning badge on the pipelines list and the settings tab. `UserPipelinesTransferHandler` answers the Users module's `UserPipelinesTransferRequestedEvent` (dispatched strictly before a deactivation or deletion that names a transfer target) with the same transfer, as the administrator of the request. A git push by another active user still takes ownership as before (`PipelineService.UpdateSyncOwnershipAsync`).

## Agent-native function steps (`type: aetheus`)

A `type: aetheus` step runs one function written in C# and shipped with the agent, instead of a versioned
shell script. The set of functions is the shared catalog (`PipelineFunctionCatalog.All`,
`src/Aetheus.Shared/Components/Pipelines/PipelineFunctionCatalog.cs:675`); the YAML spelling of a name is its
contract (names are case-sensitive, `Find` at line 700).

```yaml
      - name: Produce formatting outcome
        type: aetheus
        function: run-affected-dotnet-format
        with:
          solution: Aetheus.slnx
          dotnet: "$(DOTNET)"
          restore: "false"
        timeout_seconds: 900
```

(copied from `.pipeline/aetheus-ci.yaml:150-157`.) A function without parameters is just `type` and
`function` (`.pipeline/aetheus-candidate.yaml:271-273`, `ensure-node-runtime`).

**Syntax.**

- `type: aetheus` (`PipelineFunctionCatalog.StepType`, compared case-insensitively by the validator).
- `function:` the catalog name (`PipelineStepDefinition.Function`). Required.
- `with:` a map of string parameters (`PipelineStepDefinition.With`), keyed by the names the catalog entry
  declares. Each parameter is required or optional; the descriptor also lists the fixed `Outputs` the
  function publishes as run variables (`PipelineFunctionDescriptor`). Values accept run-variable
  substitution (`$(NAME)`), applied when the task is created.
- The usual step keys (`name`, `timeout_seconds`, `continue_on_error`, ...) apply as for any step.

**Refused when the definition is saved** (`PipelineFunctionStepValidator`,
`src/Aetheus.Back/Components/Pipelines/PipelineFunctionStepValidator.cs:13-57`, called from
`PipelineDefinitionValidator.cs:292`): no `function`; a function that is not in the catalog (the message lists
the known ones); a required `with` key missing or blank; a `with` key the function does not declare;
`function` or `with` on a step of another type. A misspelled function or parameter never reaches an agent.

**Outputs.** A later step may reference the fixed outputs of a function (`$(DOTNET)` after
`ensure-dotnet-sdk`): the unresolved-variable guard adds them to the names a run will publish
(`PipelineUnresolvedVariableGuard.cs:191-193`). One catalog entry (`publish-failed-tests`) also publishes one
variable whose name the caller gives, through `NamedOutputParameter` (`PipelineFunctionDescriptor.cs:45`); a
later step cannot reference it before the run.

**Dispatch.** The dispatcher routes `aetheus` to `PipelineFunctionTaskFactory`
(`PipelineStepTaskDispatcher.cs:308`). The factory re-checks the function and the parameters (a definition
stored before the catalog changed), substitutes the `with` values, refuses a value that still contains an
unresolved `$(NAME)` (a step with `continue_on_error` fails alone instead of stopping the run), puts the
parameters as JSON in `AETHEUS_FUNCTION_PARAMETERS_JSON` and creates a task whose target is the function name
(`PipelineFunctionTaskFactory.cs:25-85`). The task kind comes from the access level of the function: `Build`
is `PipelineFunction` and needs the agent capability `pipeline.functions`; `Deployment` is
`PipelineDeploymentFunction` and needs `deployment.functions` (`PipelineFunctionCatalog.OperationFor`,
`AgentCapabilities.cs:51,56`). The agent publishes `pipeline.functions` at enrolment
(`EnrollmentService.cs:142-148`).

**How the agent resolves a function.** `PipelineFunctionOperationExecutor`
(`src/Aetheus.Agent.Core/Functions/PipelineFunctionOperationExecutor.cs:49-73`) first checks the task against
the catalog (function, task kind, required parameters, `WORKSPACE`), then looks the name up among the
registered `IPipelineFunction` implementations. The implementations are `PipelineFunctions.CreateAll()`
(`PipelineFunctions.cs:13`), registered in `AgentCoreServiceCollectionExtensions.cs:220-221`; the same set is
what the `aetheus-fn` executable runs inside a container-isolated stage. The function runs under the step
timeout (linked cancellation token, and `WaitAsync` so a function that ignores its token still ends on time,
`PipelineFunctionOperationExecutor.cs:82-89`). Its fixed outputs are published as
`##aetheus[setvariable name=...]` lines only on success, and a success that left a declared output unset fails
(`PipelineFunctionOperationExecutor.cs:120-139`).

**Failure codes** (`PipelineFunctionOperationExecutor.cs:20-22`): `invalid-request` (the task does not match the
catalog), `function-not-implemented`, and `function-error` (the function returned a failure or threw). A
timeout is reported as timed out and an external cancellation propagates; neither reads as success.

**`function-not-implemented`.** The catalog lives in Shared and is what the control plane validates against;
the implementations live in the agent build. A function that is in the catalog but that the agent has no code
for (an agent older than the control plane) passes validation when the definition is saved, and is refused
when the agent receives the task: `Function '<name>' is in the catalog but this agent has no implementation
for it; update the agent.` (`PipelineFunctionOperationExecutor.cs:61-66`). It is a refusal at execution, not
at registration of the definition. In a container-isolated stage the same code is returned when the agent does
not ship `aetheus-fn` (`ContainerPipelineFunctionExecutor.cs:104-118`); `aetheus-fn` exits 69 for it
(`AetheusFnExitCodes.cs:19`).

**Stage isolation.** In a stage with container isolation the task carries the stage container, and the agent
runs the function inside it with `aetheus-fn`, never on the host (`PipelineFunctionTaskFactory.cs:77-83`).

## Native blue-green step types

The three deployment paths each re-implemented the same host cutover in shell and had drifted apart.
Seven typed operations express that sequence instead, dispatched through `BlueGreenStepBinding` and
executed by `BlueGreenOperationExecutor` on the agent:

| Step type | `OperationKind` | Does |
|-----------|-----------------|------|
| `bluegreen-migrate` | `BlueGreenMigrate` (613) | Bring up shared services, read the applied EF history, enforce the expand/contract gate against the **pending** migrations, then run the migration bundle once as a dedicated job |
| `bluegreen-up` | `BlueGreenUp` (614) | Select the idle colour from the persisted live colour, start it, hold until it reports ready on its own ports. Never touches the live colour |
| `bluegreen-switch` | `BlueGreenSwitch` (615) | Render the upstream template, point the web server at the idle colour and reload it. The reload is self-validating |
| `bluegreen-commit` | `BlueGreenCommit` (616) | Record the deployed revision, stop the replaced colour, clear the journal |
| `bluegreen-rollback` | `BlueGreenRollback` (617) | Put traffic back on the colour that was serving before the switch, then stop the candidate. Meant to run as a failure-condition step |
| `bluegreen-retire` | `BlueGreenRetire` (618) | Undo a first deployment that already moved traffic (no previous colour to restore): restore the recorded upstream, stop the switched colour, close the transaction. Refused when a previous colour exists, since that case is a rollback |
| `bluegreen-revert` | `BlueGreenRevert` (619) | Put traffic back on the reserve colour kept by the last commit (N-1): recorded upstream and a reload, no restart, no migration. Refused with no recorded reserve, an open transaction, or a reserve that fails its readiness probe |

Splitting the cutover across separate steps works because state lives in an on-disk journal under the
environment's state directory, not a lock held inside one long-lived process: each step reads what its
predecessor committed, so an interrupted deployment stays reconcilable. The journal is marked
**before** traffic moves, so an interruption cannot leave a real cutover that rollback would report as
"nothing to undo". Rollback only acts on a journal in the `SWITCHED` state.

`BlueGreenStepBinding` validates every field before a task is created: `project` against
`OperationTargetValidator`, `state_dir` as an absolute path with no traversal, `ports` as four
distinct valid ports (`frontBlue,backBlue,frontGreen,backGreen`), and `compose_files` / `env_file` as
required. `bluegreen-switch` additionally requires `reload_helper` (absolute, whitespace-free, since
it is invoked argv-exact through sudo), `revision`, `upstream_template` and `upstream_conf` (without
the last two the switch would reload the live configuration unchanged and report a successful
cutover). `bluegreen-migrate` requires `migrations_dir`, and a gate that cannot read it fails rather
than reading as "nothing pending". `bluegreen-rollback` and `bluegreen-revert` require `upstream_conf` and `reload_helper`.
An unrecognised `bluegreen-*` type maps to `OperationKind.None` and is refused, so a typo cannot
become a commit against a live environment.

Capabilities: the seven blue-green operations sit behind the `deployment.apply` grant alongside
`PipelineDeploy`; `smoke` only probes an already-deployed origin, so it uses the `pipeline.build`
capability instead. That gate is enforced when an agent *claims* the task (`AgentCapabilities.RequiredFor`
→ `TaskRepository`), not when the stage's server is chosen: `PipelineDispatchServerResolver.StageHasDeployStep`
matches `type: deploy` only, and `execution_role: deploy` is a workload classification, not a selector.
A blue-green stage must therefore point at a deployment-capable host through its own
`environment:`/`pool:`/`agent:` selectors (`host-bluegreen-deploy@2` leaves that to the consuming
pipeline; `aetheus-deploy-prod` uses `environment: prod`). Land it on a plain runner and its tasks are
simply never claimed.

**`compose_env:` is how per-run values reach Compose.** A shell cutover exported the image tags, the
application version and the source revision into the process it then ran `docker compose` from. A
typed step has no such process, and the agent service environment carries none of them, so Compose
would substitute the defaults written in the Compose file, starting `aetheus-back:local` while the
step reported a successful cutover. The step lists the run variables it needs; the control plane
resolves them and refuses a name the run does not define, and the agent forwards exactly that list to
the `docker compose` child process. Everything else in the task environment, vault secrets included,
stays out of it.

**The bootstrap identity is derived, not transported.** A deployment hands the candidate colour a
self-expiring admin identity so its authenticated smoke exercises real JWT, RBAC and SignalR paths
without touching production data or creating a persisted account. A shell cutover generated it in the
process that then started the containers and ran the probe, so it existed nowhere else. Typed steps
break that: the colour is started by one task and probed by another, and the only channel between two
tasks (a run variable published with `setvariable`) is echoed into the run log and readable by
anyone with `Project.Read`. `SecretMaskingService` redacts vault secrets and runtime-registered ones,
not a value it has never been told about.

So `DeploymentBootstrapIdentity` derives it from the run id under an HKDF-separated key
(`Deployment:BootstrapIdentityKey`, falling back to `Auth:EncryptionKey`; **no key configured fails
closed**, because the run id is public and an empty-keyed HMAC would make the credential predictable).
`bluegreen-up` and `smoke` compute the same value independently, and the control plane registers it as
a runtime secret so it is redacted in logs, in the recorded command and in step output variables. A
pipeline that names any of these variables itself wins: the demo authenticates with the credential it
publicly advertises, and overwriting that with a synthetic one would probe an identity nobody can use.

**The reload helper must be a single argv element.** `bluegreen-switch` invokes it as
`sudo -n <path>`, and the binding refuses a value containing whitespace, because the path comes from
a pipeline variable and spaces would turn the sudo grant into an arbitrary privileged command. The
agent's three-word `systemctl reload apache2.service` grant is therefore unreachable from a typed
step; `install-agent-linux.sh` provisions `/usr/local/lib/aetheus/aetheus-apache-reload` (no
arguments, configtest before apply) and grants that exact path. **A host must have re-run the
installer before it can serve a native switch.**

**`type: smoke` produces evidence; `evidence_gate:` decides what it costs.**
`SmokeOperationExecutor` probes readiness (with an optional response-time budget), an optional
frontend cache contract and an optional authenticated browser suite against an **origin** (never a
full URL, so a definition cannot repoint a probe). It always publishes `SMOKE_GATE_STATUS` (`0` clean,
`1` findings), `SMOKE_FINDINGS` and `SMOKE_BLOCKING_FINDINGS`. A technical fault that produces no
evidence, such as a browser image that cannot start, always fails the step.

`evidence_gate: advisory` is the default and the behaviour every existing caller gets: findings are
recorded and the step still exits 0. `evidence_gate: blocking` fails the step on a *blocking* finding
(readiness that does not answer, a broken frontend cache contract, a failing browser suite), which is
what lets a `failed()` stage compensate a bad cutover instead of merely annotating it. **The
response-time budget is never blocking in either mode**: a cold cache or a busy host is enough to
exceed it, and that is exactly what once rolled back a deployment that was serving correctly.
`host-bluegreen-deploy` sets `blocking`, because its `Rollback` stage would otherwise be unreachable.

**No pipeline consumes `SMOKE_GATE_STATUS`, and that is the decision, not an oversight.** It was
introduced as the value a deployment gate would read to decide whether findings should stop a
cutover. `evidence_gate: blocking` answers that question in the step itself, which is strictly better:
a gate reading a variable can only act after the step returned success, whereas a blocking step fails
in place and hands control to the compensation stage. The three variables stay published as recorded
evidence (a run page shows how many findings a deployment had, and whether any was blocking, without
having to re-read the log), but nothing branches on them. Adding a consumer would mean two places
deciding the same thing, which is how the shell paths drifted in the first place.

`deploy/pipeline-templates/host-bluegreen-deploy-v2.yaml` composes these types into a reusable cutover
parameterised by the environment it acts on, and is registered in `DeliveryPipelineTemplateSeeder`
under `host-bluegreen-deploy`. v1 stays published and immutable (the seeder throws on a version whose
content changed), and v2 is the one to extend: it declares no `revision` run parameter, `BG_REVISION` defaults to the
launch commit (`$(BUILD_SOURCEVERSION)`) and a consumer overrides it, and a revision the run cannot resolve is refused
by `bluegreen-migrate`/`bluegreen-switch` instead of being written to the journal.
`aetheus-nightly`, `aetheus-demo` and `aetheus-deploy-prod` extend it. `aetheus-release-fast` keeps its shell cutover
**by design**, as an emergency path independent of the engine, otherwise that engine would have to be
working in order to deploy a fix to it.

What stays outside the template on the production path is what no generic cutover can know: the
candidate contract, secret-zero and the production state directory (the `prod-deploy-prepare.sh` heredoc
program of `.pipeline/aetheus-deploy-prod.yaml`), the WASM runtime contract (its `Verify the frontend runtime
contract` step, an application that answers every health probe and then fails at the first data grid), and the
vitrine published on the same host (its `publish-vitrine.sh` heredoc program, snapshotted before publication because
`bluegreen-rollback` restores the application colour and the upstream configuration and knows nothing about a static site).

## Per-pipeline build number

`BUILD_BUILDID` is the globally monotonic run id, so an application version derived from it jumps
between builds of the same pipeline. `PipelineBuildNumberRepository.ReserveNextBuildNumberAsync`
reserves a per-pipeline counter under a row lock and snapshots it onto the run;
`PipelineVariableResolver` exposes it to steps as `BUILD_PIPELINE_RUNNUMBER`. The counter lives on
`Pipeline` rather than being derived from a run count, so retention purging old runs can never rewind
it, and the `AddPipelineBuildNumber` migration seeds it from each pipeline's existing run count. The
reservation joins an ambient transaction when one is already open, because the webhook trigger path
runs inside one.

`aetheus-ci` reads `BUILD_PIPELINE_RUNNUMBER` in shell and falls back to the run id when the deployed
control plane does not serve it. `aetheus-deploy-prod`, `aetheus-nightly`, `aetheus-demo` and
`aetheus-release-fast` declare it directly in their `variables` block, which is only safe because the control plane that
resolves it is deployed: a `variables` block cannot fall back, and an unknown `$(...)` is left
literal rather than emptied, so it would be baked verbatim into the images as the version.
`DeploymentPipelines_DeriveTheirVersionFromThePerPipelineCounter` freezes that contract.

## Blue-Green Deployment Safety

The rules below govern the shell-based blue-green cutover the deployment pipelines still use. Blue-green
pipelines that use Docker Compose must distinguish **colour-scoped services** from
**shared services** such as PostgreSQL, persistent storage, or a message broker.

- A Compose service without `profiles:` is enabled for every profile. Consequently,
  `docker compose --profile <old-colour> stop` without explicit service names also stops shared,
  unprofiled services. Never use an unscoped `stop`, `down`, `restart`, or `rm` in a blue-green
  pipeline.
- Stop only the old colour's named services, for example:

  ```sh
  docker compose --profile "$LIVE" stop "back-$LIVE" "front-$LIVE"
  ```

- Use `/health/live` only to answer "is the process running?". A deployment gate must use
  `/health/ready`, which includes required dependencies such as PostgreSQL. Liveness alone can
  remain green while every DB-backed request fails.
- The safe order is: start the idle colour and shared dependencies, wait for backend readiness,
  verify the idle frontend, switch the reverse-proxy upstream, record the live colour, run the
  public smoke while the old colour is still available, then stop only the two old-colour
  application services. A failed public smoke must restore the old upstream and live-colour file.
- A fast pipeline may skip functional CI/QA, but it must never skip infrastructure readiness or
  weaken service targeting. Schema changes still require the full release path and expand/contract
  compatibility.
- **No test guards these rules.** `.pipeline/**` is deliberately out of the Architecture Guard Tests'
  scope (see `AGENTS.md`), so the explicit old-colour stop and the readiness probes in
  `.pipeline/aetheus-deploy-prod.yaml` and `deploy/compose/remote-bluegreen.compose.yml` are held by
  review and by running the pipeline, not by asserting on its YAML. Editing either file means
  re-reading this list.

The production incident that motivated these rules and the recovery procedure are documented in
[`docs/self-deploy-guide.md`](../../../../docs/self-deploy-guide.md#5-chronologie-condensee-references).

## API Surface

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/pipelines` | GET | User | List pipelines (paginated) |
| `/api/pipelines/dependencies` | GET | User | Resource-filtered parent/leaf dependency view |
| `/api/pipelines/dependencies/page` | GET | User | Paginated resource-filtered dependency graph |
| `/api/pipelines/favorites` | GET | User | Caller-visible favorite pipelines |
| `/api/pipelines/{pipelineId}/favorite` | PUT | User | Set or clear a pipeline favorite |
| `/api/pipelines/{id}` | GET | User | Pipeline detail |
| `/api/pipelines/{id}/source` | GET | User | Effective Git repository, branch, path and commit |
| `/api/pipelines` | POST | User | Create pipeline (YAML validated) |
| `/api/pipelines/{id}` | PUT | User | Update pipeline |
| `/api/pipelines/{id}` | DELETE | Admin | Delete pipeline |
| `/api/pipelines/{id}/parameters` | GET | User | Parametres declaratifs disponibles au lancement. Le front lance sur les valeurs par defaut declarees **sans dialogue** ; il n'en ouvre un (`RunParametersDialog`) que si un parametre `required` n'a pas de defaut, ce qui preserve le contrat M-051 sans cas particulier (`candidateVersion` est declare `required` sans defaut). L'entree « Executer avec des options » ouvre le dialogue unifie `PipelineLaunchDialog`, branche et parametres ensemble ; les deux partagent `RunParameterFields` et affichent les dernieres releases du projet comme aide au choix. |
| `/api/pipelines/{id}/run` | POST | User+ServerAdmin | Trigger with optional `parameters`, `sourceBranch`, and per-pipeline `idempotencyKey` (max 64); replay returns the existing run |
| `/api/pipelines/{id}/preflight` | POST | User+ServerAdmin | Pre-flight target resolution |
| `/api/pipelines/{id}/dry-run` | POST | User+ServerAdmin | Dry-run with variable resolution |
| `/api/pipelines/{id}/runs` | GET | User | List runs (paginated) |
| `/api/pipelines/runs/active` | GET | User | List active runs, optionally filtered by project |
| `/api/pipelines/runs/recent` | GET | User | List recent runs, optionally filtered by project |
| `/api/pipelines/runs/{runId}` | GET | User | Run detail |
| `/api/pipelines/runs/{runId}/queue` | GET | User | État courant de la file et du runner affecté au run |
| `/api/pipelines/runs/{runId}/checkpoint-resume-preview` | GET | User | Prévisualisation des checkpoints réutilisables et des sous-pipelines à rejouer |
| `/api/pipelines/runs/{runId}/lineage` | GET | User + Pipeline Read | Run lineage tile (recette R-498): the run that launched this one and the runs it started. Downstream runs of a pipeline the caller cannot read are left out |
| `/api/pipelines/{id}/runs/{runId}/stage-baselines` | GET | User + Pipeline Read | Usual duration of each stage and step, averaged over the pipeline's last successful runs |
| `/api/pipelines/runs/active-workspaces` | GET | AgentToken | Workspace slots of the runs still in flight, so an agent can reclaim the disk of the others |
| `/api/pipelines/approvals/pending` | GET | User | Every approval still pending on a caller-readable pipeline |
| `/api/pipelines/setup/readiness` | POST | User + Project Write | Setup wizard: what would stop the pipelines about to be created from running |
| `/api/pipelines/setup/unmet` | GET | User + Project Read | Libraries and vaults the project's pipelines require and the project lacks |
| `/api/pipelines/setup/provision` | POST | User + Project Write + Library/Vault Write | Create the libraries and vaults the selected templates require, keys with empty values |
| `/api/pipelines/setup/provision-unmet` | POST | User + Project Write + Library/Vault Write | Create what `setup/unmet` reports for the existing pipelines |
| `/api/pipelines/runs/{runId}/cancel` | POST | User | Cancel a run and its active triggered descendants recursively |
| `/api/pipelines/runs/{runId}/retry-failed` | POST | User+ServerAdmin | Retry failed steps. A refusal is a `409` naming its own reason (`ConflictException`), not a bare `404`: the three refusals used to share one silent `false`, so an unretryable run looked exactly like a retry that had been attempted and failed. The refusal raised when nothing can be reset says that a run replays the YAML captured when it was triggered ([ADR-015](../../../../docs/adr/ADR-015-git-strict-project-pipelines.md)) and points at **Re-run** for a definition that has since been edited. |
| `/api/pipelines/runs/{runId}/rerun` | POST | User+ServerAdmin | Relancer depuis la définition courante, un snapshot source ou une reprise explicite par checkpoints vérifiés |
| `/api/pipelines/runs/{runId}/artifacts` | GET | User | List artifacts |
| `/api/pipelines/runs/{runId}/artifacts` | POST | AgentToken | Publish artifact |
| `/api/pipelines/runs/{runId}/approvals` | GET | User | List approvals |
| `/api/pipelines/approvals/{id}/decide` | POST | User | Approve/reject |
| `/api/pipelines/runs/{runId}/test-results` | GET | User | Test results |
| `/api/pipelines/runs/{runId}/test-results` | POST | AgentToken | Publish test results |
| `/api/pipelines/runs/{runId}/coverage` | GET | User | Coverage summary |
| `/api/pipelines/runs/{runId}/coverage/assemblies` | GET | User | Paginated assembly and file coverage |
| `/api/pipelines/runs/{runId}/coverage` | POST | AgentToken | Publish Cobertura coverage |
| `/api/pipelines/runs/{runId}/coverage/raw` | POST | AgentToken | Publish raw Cobertura XML (`application/xml`; optional stage/step query metadata) |
| `/api/pipelines/runs/{runId}/coverage-trend` | GET | User | Coverage trend across recent runs |
| `/api/pipelines/projects/{projectId}/quality-trend` | GET | User | Tendance qualite agregee du projet |
| `/api/pipelines/runs/{runId}/metrics-trend` | GET | User | Tendance des metriques de complexite du run |
| `/api/pipelines/runs/{runId}/complexity` | POST | AgentToken | Publish Roslyn complexity/LOC/CRAP metrics |
| `/api/pipelines/runs/{runId}/lint` | GET | User | Lint (SARIF) summary |
| `/api/pipelines/runs/{runId}/lint` | POST | AgentToken | Publish SARIF lint report |
| `/api/pipelines/validate` | POST | User | Validate YAML |
| `/api/pipelines/webhook` | POST | Anonymous | Webhook trigger (rate-limited) |
| `/api/pipelines/templates` | GET | User | List templates |
| `/api/pipelines/templates/{id}` | GET | User | Template detail |
| `/api/pipelines/templates/{id}/versions` | GET | User | Paginated immutable template versions |
| `/api/pipelines/templates/{id}/versions/{version}` | GET | User | Exact immutable template version |
| `/api/pipelines/templates` | POST | Admin | Create template |
| `/api/pipelines/templates/{id}` | PUT | Admin | Update template |
| `/api/pipelines/templates/{id}` | DELETE | Admin | Delete template |
| `/api/pipelines/templates/{id}/export` | GET | User | Export template as YAML |
| `/api/pipelines/templates/import` | POST | Admin | Import template from file |
| `/api/pipelines/templates/{id}/resolve` | POST | User | Resolve template parameters |
| `/api/pipelines/fleet` | GET | User | List the caller-visible pipeline fleet |
| `/api/pipelines/fleet/filter-values` | GET | User | Template names offered by the fleet's Template column filter, in the fleet's scope |
| `/api/pipelines/{id}/fleet-item` | GET | User | Fleet item detail |
| `/api/pipelines/{id}/fleet-update/preview` | POST | User + Pipeline Write + Template Read | Preview a template-based fleet update |
| `/api/pipelines/{id}/fleet-update` | POST | User + Pipeline Write + Template Read | Apply a template-based fleet update |
| `/api/pipelines/{id}/extract-template` | POST | User + Pipeline Write + Template Write | Extract a pipeline as a template |
| `/api/pipelines/{id}/promote-template` | POST | User + Pipeline Write + Template Write | Promote a pipeline template |
| `/api/pipelines/{id}/promote-template/preview` | GET | User + Pipeline Write + Template Write | Preview template promotion |

## Key Classes

- `PipelinesController` -- run/artifact/approval/webhook endpoints
- `PipelineTemplatesController` -- template CRUD + import/export
- `PipelineCheckpointController` -- verified checkpoint state for resume-from-checkpoint reruns
- `PipelineFavoritesController` -- per-user pipeline favorites
- `PipelineFleetController` -- template-based fleet update preview/apply
- `IPipelineService` / `PipelineService` -- pipeline CRUD, YAML validation
- `IPipelineRunService` / `PipelineRunService` -- run orchestration, dry-run, preflight, task dispatch (pure helpers extracted to the static `PipelineRunHelpers`). Keeps the mutually recursive engine (trigger ↔ stage advancement ↔ task creation) and, with it, **every decision about a run's fate**: only the engine advances a stage or finalizes a run.
- **Step task factories** ([ADR-041](../../../../docs/adr/ADR-041-step-factories-return-the-engine-decides.md)) -- a factory builds a task and returns; it may mark its own step failed (`PipelineRunHelpers.MarkSystemStepFailed`) when an input is unresolvable, but never advances a stage nor finalizes a run. They are registered in `PipelinesModuleExtensions`:
  - `IPipelineStepTaskBuilder` / `PipelineStepTaskBuilder` -- build the task, protect its environment, track it, mark the step dispatched. It also owns the ordinary shell step (`CreateCommandTask`) and carries the stage's container-isolation warnings. It is the seam the engine and `PipelineAnalysisTaskFactory` share; the deployment, host-operation, artifact and scanner factories still assemble their `ServerTask` themselves (each decorates it with a lease, an isolation policy or a bespoke env before handing it over).
  - `IPipelineAnalysisTaskFactory` / `PipelineAnalysisTaskFactory` -- the analysis gate plus the four publish steps (observability bundle, coverage, lint, complexity). `CreateAnalysisGateTask` returns `null` on success, else the validation message the engine fails the run with.
  - `IPipelineDeploymentTaskFactory` / `PipelineDeploymentTaskFactory` -- the cross-agent `type: deploy` step.
  - `IPipelineHostOperationTaskFactory` / `PipelineHostOperationTaskFactory` -- host configuration: `apache-proxy`, `apache-config`, `certbot`, `smoke`, and the blue-green cutover types.
  - `IPipelineArtifactTaskFactory` / `PipelineArtifactTaskFactory` -- the two steps that bring something back onto the runner: `restore-artifacts` and `restore-backup`. Its `ResolveArtifactSourceAsync` is public because the `release` step resolves the same thing; `artifacts` (collection) and `release` are still built by the engine.
  - `IPipelineScannerTaskFactory` / `PipelineScannerTaskFactory` -- `scanner` steps and their DAST lease policy.
  - `IPipelineDotnetTestTaskFactory` / `PipelineDotnetTestTaskFactory` and `IPipelineGateStatusTaskFactory` / `PipelineGateStatusTaskFactory` -- the `dotnet-test` and `gate-status` steps; like the analysis gate, they return `null` on success, else the validation message the engine fails the run with.
  - `IPipelineSystemTaskFactory` / `PipelineSystemTaskFactory` -- the injected `System:Prepare` (clone the pinned commit) and `System:Cleanup` (purge the workspace) tasks.
- `IPipelineBranchAdvanceStep` / `PipelineBranchAdvanceStep` -- the backend-run `advance-branch` step (see above); not a factory, it writes the ref itself and marks its own step.
  - `IPipelineEnvironmentCheckGuard` / `PipelineEnvironmentCheckGuard` -- the per-stage environment-check gate (P-22): every required check on the stage's environment must pass, and it is where the SSRF guard on REST callbacks lives. It touches no run state, no dispatch and no stage advancement, which is why it came out whole.
  - `IPipelineDispatchServerResolver` / `PipelineDispatchServerResolver` -- resolves the server a stage dispatches to, honouring a run-scoped local deployment target, and owns the fail-closed deploy policy. `StageHasDeployStep` matches **`type: deploy` only**: it is what routes a stage to a deployment-capable host. The `bluegreen-*` operations are still gated on `deployment.apply` when an agent claims their task (`AgentCapabilities.RequiredFor`, enforced in `TaskRepository`), so a blue-green stage must point at a deployment-capable host through its own `environment:`/`pool:`/`agent:` selectors or its tasks stay unclaimed. `smoke` needs `pipeline.build`.
- `PipelineCommandBuilder` -- static helper: OS-specific shell command construction for pipeline steps and system tasks
- `IPipelineVariableResolver` / `PipelineVariableResolver` -- resolves the full variable set (system, inline YAML, libraries, vaults, cross-access) with warnings and secret-key tracking
- `IPipelineGitService` / `PipelineGitService` -- git-strict storage for project-owned pipeline definitions (read/write `.pipeline/*.yaml`, env→project copy; internal repos only)
- `IPipelineApprovalService` / `PipelineApprovalService` -- gate approvals
- `PipelineOwnerAuthorization` -- authorizes the **whole** owner triple a create/update request names (project, environment, project server), not just the project. A project server is resolved to its owning project because there is no `ResourceType.ProjectServer`; an owner that cannot be resolved is refused, never skipped. Called by both write paths of `PipelinesController`.
- `IPipelineArtifactService` / `PipelineArtifactService` -- artifact + test-result + coverage storage
- `PipelineArtifactRepository` -- focused artifact persistence collaborator used by `PipelineCoreRepository`
- `PipelineBuildNumberRepository` -- reserves the per-pipeline build counter (`BUILD_PIPELINE_RUNNUMBER`) under a row lock, joining an ambient transaction when one is open
- `BlueGreenStepBinding` -- validates a `bluegreen-*` step definition and turns it into the agent's task environment before any task is created
- `CoverageResultParser` -- Cobertura XML parsing into `CoverageResult` entities
- `IPipelineTemplateService` / `PipelineTemplateService` -- template management
- `IPipelineWebhookService` / `PipelineWebhookService` -- webhook signature verification
- `IPipelineRepository` / `PipelineRepository` -- facade composing `PipelineCoreRepository` (core data access, with artifact persistence delegated to `PipelineArtifactRepository`), `PipelineServerResolver` (server resolution, org-fallback, effective-project-id), `PipelineCoverageRepository` (coverage queries), `PipelineLifecycleRepository` (retention/lifecycle), and `PipelineDependencyGraphRepository` (dependency graph queries)
- `PipelineSchedulerService` -- background: cron-triggered runs
- `PipelineRefusedLaunchRecorder` -- recette R-522: a refused automated launch (schedule, push, webhook) has nobody to read the refusal, so it is recorded as a run that failed at once and carries the reason in its warnings, with an audit line (`LaunchRefused`) and the `pipeline.launch-refused` notification. The launcher calls it for a refused preparation (`PrepareAutomatedRunAsync`) and for a refused launch (`TriggerPreparedAutomatedRunAsync`). Inside an open transaction (`IDbTransactionScope.InTransaction`) it records nothing, since the caller's rollback would erase the run after its events had gone out: the webhook, which launches inside one, records the refusal once its transaction has rolled back, so it is recorded exactly once. Once the run is saved, the audit line, the live event and the notification are best effort (a failure is logged and never replaces the refusal the caller rethrows). A refused interactive launch still creates no run
- `PipelineRetentionService` -- background: old-run cleanup
- `PipelineTriggerReconcileService` -- background trigger reconciliation; scheduler, retention and reconcile use `PostgresLeaderLease` so only one blue-green replica executes each workload. Beyond the trigger passes it also expires stale approvals (`ExpireStaleApprovalsAsync`) and frees runs stranded on an approval that is no longer `Pending` (`ResolveStrandedApprovalsAsync`). The stranded pass only **re-applies a decision already recorded** -- approved resumes, rejected/timed-out fails -- and leaves a run with no approval row at all to a human, logged, rather than deciding on a background sweep's authority. It is bounded on both sides: `GetStrandedApprovalRunsAsync` returns at most 50 runs per sweep, and a run that keeps failing to leave the state is abandoned after 5 consecutive attempts with one `LogError`, instead of one `LogCritical` per tick forever.

## Canonical source and versioned configuration (2026-07-22)

This section supersedes the historical DB-fallback wording above for workspace runs. A project pipeline binds to one canonical `SourceRepositoryId`; the same repository supplies `.pipeline/<name>.yaml`, the checkout URL, and the full immutable commit. Existing mono-repository projects are backfilled automatically. A multi-repository project without an explicit binding, or any workspace pipeline without a clone URL and full SHA, fails before a run is created.

The `apache-config` step maps safe destination filenames to templates under `.pipeline/configs/**`. Templates are read from the run's exact SHA, render only strict `#{VAR}#` tokens, and are sent as one encrypted `ApacheApplyConfigSet` (311). The agent snapshots affected files and enabled links, runs the fixed privileged `configtest`, reloads once, and restores/reloads the previous set if either command fails. This path requires an installer upgrade with `--enable-apache-manage` so the root-owned no-argument helper exists.

A definition may take its workspace from another repository of the project (`source:` block, recette R-534, ADR-012 amendment of 2026-09-30): `PipelineWorkspaceSourceResolver` fetches it when it is a mirror, pins its branch head and, with `must_match_definition`, refuses the run unless its tree is the tree of the definition's revision. The definition, its templates and `.pipeline/configs/**` stay on the canonical repository; their revision is kept on the run as `AETHEUS_DEFINITION_COMMIT`. A same-commit or checkpoint-resume rerun of such a run (`PipelineRunLauncher.RerunAsync`) reads the definition at that recorded revision and pins the workspace to the run's own `CommitHash` in the source repository (`PrepareRunAsync` `workspaceCommitOverride`, `ResolveAsync` `pinnedCommit`) instead of its branch head; the rerun is refused when that commit is no longer in the source repository. A `trigger` step that materialises a missing target pipeline reads its definition at the same definition revision (`PipelineTriggerStepCoordinator`), never at the workspace commit of another repository.

Manual run requests carry an optional idempotency key. The database enforces uniqueness per pipeline and a replay returns the existing run without dispatching tasks twice. A declared but missing Vault blocks dry-run, preflight, and launch; missing variable libraries remain warnings.

## Cross-Module Dependencies

- Depends on: AiTasks (profile resolution and AI result orchestration), Audit, Vaults (secret injection), VariableLibraries, Settings (webhook config), Git (`IGitBranchAdvanceService` for `advance-branch`, mirror refresh and tree listing for `source:`), Shared
- Depended on by: Tasks, Git, Releases, Artifacts (storage/retention), Analysis (run ownership and gate aggregation)
