// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

public sealed record DryRunResultDto
{
    public List<DryRunStageDto> Stages { get; init; } = [];
    public Dictionary<string, string> ResolvedVariables { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed record DryRunStageDto
{
    public string StageName { get; init; } = string.Empty;
    public string Agent { get; init; } = string.Empty;
    public string? Os { get; init; }
    public List<DryRunStepDto> Steps { get; init; } = [];
}

public sealed record DryRunStepDto
{
    public string StepName { get; init; } = string.Empty;
    public string OriginalCommand { get; init; } = string.Empty;
    public string ResolvedCommand { get; init; } = string.Empty;
}

/// <summary>Result of resolving each stage's target server <em>before</em> launching a run,
/// so the UI can warn when no online agent matches a stage.</summary>
public sealed record PipelinePreflightDto
{
    public List<PreflightStageDto> Stages { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed record PreflightStageDto
{
    public string StageName { get; init; } = string.Empty;
    public PreflightTargetKind TargetKind { get; init; }

    /// <summary>The agent name / pool name / environment name the stage targets.</summary>
    public string Target { get; init; } = string.Empty;
    public bool Resolved { get; init; }
    public string? ServerName { get; init; }

    /// <summary>Human-readable reason when <see cref="Resolved"/> is false.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// How far down the trigger chain this stage lives: 0 for the pipeline being launched, 1 for a
    /// pipeline it triggers, and so on.
    ///
    /// Without it the dialog listed only the launching pipeline's own stages, which on an
    /// orchestrator like aetheus-candidate is four trigger steps and nothing else: the user was
    /// shown a preview of a run whose actual work was invisible.
    /// </summary>
    public int Depth { get; init; }

    /// <summary>The pipeline this stage belongs to, or null for the one being launched. The launch
    /// dialog prefixes the stage badge with it, so a blocked "Deploy" says which pipeline it is in.</summary>
    public string? PipelineName { get; init; }
}

/// <summary>
/// One requirement the blocking preflight actually verified before a run was allowed to start,
/// snapshotted onto the run.
///
/// The refusal path is loud (the caller gets a 400, the audit trail gets an entry) but the ACCEPTED
/// path left no trace at all: a run that later died on an environment or a missing runner gave no way
/// to tell whether the preflight had looked at that thing and found it fine, or had never looked.
/// </summary>
public sealed record PreflightCheckDto
{
    /// <summary>What class of requirement this is: <c>runner</c>, <c>environment</c>,
    /// <c>pipeline-reference</c>, <c>ports</c>, <c>child-pipeline</c>, <c>scanner-manifest</c>,
    /// <c>release-artifact</c> or <c>ai-profile</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>What was checked: a stage's selector, an environment name, a pipeline name.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>False for a requirement that refused the launch. A recorded check is normally
    /// satisfied, since an unsatisfied one means no run was created at all; it is false only on a
    /// run whose refusals were tolerated.</summary>
    public bool Satisfied { get; init; } = true;

    /// <summary>The refusal text when <see cref="Satisfied"/> is false.</summary>
    public string? Detail { get; init; }
}

/// <summary>Everything the blocking preflight concluded: what it refused, what it verified, and what
/// it noted without refusing.</summary>
public sealed record PipelinePreflightOutcomeDto
{
    public List<string> Problems { get; init; } = [];
    public List<PreflightCheckDto> Checks { get; init; } = [];

    /// <summary>Records an undeclared observed listener under observed_port_policy: warning without
    /// refusing the run. Keep it separate from <see cref="Problems"/> so a warning cannot be
    /// mistaken for a refusal.</summary>
    public List<string> Warnings { get; init; } = [];
}
