// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data.Entities;

/// <summary>
/// What the expand/contract gate of a blue-green migrate step concluded on one run (recette R1-43).
/// Before this row the verdict lived only in the step log, which retention trims and nobody reads twice.
/// One row per gate evaluation; the per-migration detail is kept as JSON because it is only ever read
/// whole, for the run page.
/// </summary>
public class PipelineMigrationGateVerdict
{
    public int Id { get; set; }
    public int PipelineRunId { get; set; }

    /// <summary>Stage of the migrate step that ran the gate.</summary>
    public string? StageName { get; set; }

    /// <summary>The migrate step that ran the gate.</summary>
    public string? StepName { get; set; }

    public MigrationGateOutcome Outcome { get; set; }

    /// <summary>Pending migrations the gate found, including any past the listed ceiling.</summary>
    public int PendingCount { get; set; }

    /// <summary>Why the gate could not run (<see cref="MigrationGateOutcome.Unavailable"/>); null otherwise.</summary>
    public string? Detail { get; set; }

    /// <summary>The inspected migrations (<c>MigrationGateMigrationVerdict</c> list) in JSON.</summary>
    public string MigrationsJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; }

    // Navigation
    public PipelineRun PipelineRun { get; set; } = null!;
}
