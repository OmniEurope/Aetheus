// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data.Entities;

public class PipelineRun
{
    public int Id { get; set; }
    public int PipelineId { get; set; }
    public PipelineStatus Status { get; set; } = PipelineStatus.Pending;
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string AdditionalVariablesJson { get; set; } = "{}";
    public string? ResolvedVariablesJson { get; set; }
    public string? WarningsJson { get; set; }

    /// <summary>Queue-time run parameters (P): JSON of the effective name→value pairs the user supplied
    /// (or the parameter defaults) at launch. "{}" when the pipeline declares no <c>parameters:</c>.
    /// Snapshotted so the run view and the rerun dialog can show/replay exactly what was launched.</summary>
    public string ParametersJson { get; set; } = "{}";

    /// <summary>
    /// The exact pipeline YAML executed by this run, captured at trigger time. For project-owned
    /// pipelines this is the git-strict definition read from the project repo's <c>.pipeline/</c>;
    /// makes the run reproducible/auditable even after the definition later changes. Null for runs
    /// created before snapshotting existed.
    /// </summary>
    public string? YamlSnapshot { get; set; }

    /// <summary>Branch this run built against (resolved at trigger time from the project's
    /// default branch or the triggering push). Null for runs created before git context existed.</summary>
    public string? BranchName { get; set; }

    /// <summary>Commit SHA this run built against. Workspace runs require a full immutable SHA
    /// before creation; null is reserved for pipelines that do not need a repository workspace.</summary>
    public string? CommitHash { get; set; }

    /// <summary>The canonical clone URL selected together with the authoritative pipeline YAML.
    /// Snapshotted so every stage checkout uses the same repository even if project settings change.</summary>
    public string? RepositoryUrl { get; set; }

    /// <summary>Caller-supplied key used to return the original run when a launch request is replayed.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Per-pipeline sequential build number reserved from <see cref="Pipeline.BuildCounter"/> at
    /// launch, exposed to steps as <c>BUILD_PIPELINE_RUNNUMBER</c>. Unlike the globally monotonic
    /// run id (<c>BUILD_BUILDID</c>) this increments by exactly one per run of the same pipeline,
    /// which is what an application version pattern needs. 0 for runs created before it existed.
    /// </summary>
    public int BuildNumber { get; set; }

    /// <summary> The project release number this run builds with, exposed to steps as
    /// <c>BUILD_RELEASENUMBER</c>. Taken at launch from the counter of the run's
    /// <see cref="ReleaseSeries"/> (<see cref="ProjectReleaseSeries"/>): the
    /// next one when the pipeline declares <c>release_number: allocate</c>, the run it was started
    /// by when it has one in the same project (a candidate's CI inherits the candidate's number),
    /// the latest otherwise. Null for a pipeline without a project and for runs created before it
    /// existed. </summary>
    public int? ReleaseNumber { get; set; }

    /// <summary>The version series (<c>major.minor</c> of the commit's <c>VersionPrefix</c>) the
    /// <see cref="ReleaseNumber"/> counts in, exposed to steps as <c>BUILD_RELEASESERIES</c>: the
    /// application version is this series, a dot and the number. Empty when the code declares no
    /// <c>VersionPrefix</c>; null for a run without a release number.</summary>
    public string? ReleaseSeries { get; set; }

    /// <summary>
    /// Why the last scheduling pass dispatched nothing, in one human sentence per blocked stage
    /// (group throttle, runner or deployment target offline, artifact collection in flight). Null
    /// while the run is progressing. Without it a run sits in <c>Running</c> with no task moving and
    /// the reason exists only inside the planner's local state, which is exactly the case that sends
    /// someone to the server logs.
    /// </summary>
    public string? WaitingReason { get; set; }

    /// <summary>When the current <see cref="WaitingReason"/> started. Reset only when the reason text
    /// changes, so "waiting since" measures this wait and not the last scheduling pass.</summary>
    public DateTime? WaitingSince { get; set; }

    /// <summary>
    /// What the blocking preflight verified at launch (runners, environments, referenced pipelines,
    /// ports, and each pipeline of the triggered chain), as a JSON list of
    /// <c>PreflightCheckDto</c>. Null for runs created before it was recorded, and for runs created
    /// on a path that does not run the preflight.
    ///
    /// A refusal never reaches this column: a refused interactive launch creates no run, and the failed
    /// run that records a refused automated launch carries its reason in <see cref="WarningsJson"/>. What
    /// it answers is the opposite question, the one that had no answer: a run that died four stages in on an
    /// environment gave no way to tell whether the preflight had checked that environment and found
    /// it fine, or had never looked at it.
    /// </summary>
    public string? PreflightJson { get; set; }

    /// <summary>
    /// Launched in "run to end" mode, chosen at manual launch when the platform setting allows it: every
    /// step continues on error so one run exercises everything that can still run. A failure still
    /// ends the run Failed (<see cref="PipelineStepRun.ContinueOnErrorForced"/>), and once a step has
    /// failed the run may not publish a release.
    /// </summary>
    public bool RunToEnd { get; set; }

    // Navigation
    public Pipeline Pipeline { get; set; } = null!;
    public List<PipelineStepRun> StepRuns { get; set; } = [];
    public List<ServerTask> Tasks { get; set; } = [];
    public List<TestResult> TestResults { get; set; } = [];
    public List<CoverageResult> CoverageResults { get; set; } = [];
    public List<LintResult> LintResults { get; set; } = [];
    public List<RunMetric> RunMetrics { get; set; } = [];
    public List<PipelineMigrationGateVerdict> MigrationGateVerdicts { get; set; } = [];
    public List<PipelineArtifact> Artifacts { get; set; } = [];

    /// <summary>Analysis evaluations recorded against this run. The inverse of an existing foreign key,
    /// so it adds no column and no migration; it exists so run listings can project the gate grade in
    /// the same query instead of computing a gate per row.</summary>
    public List<AnalysisEvaluation> AnalysisEvaluations { get; set; } = [];
}
