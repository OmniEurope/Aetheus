// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// The verdict the blue-green migrate step publishes for the control plane to keep (recette R1-43).
/// The agent writes it as one <see cref="Directive"/> log line followed by this record in JSON; the
/// back reads that line when the task completes and stores it against the run.
/// </summary>
public sealed record MigrationGateReport
{
    /// <summary>Prefix of the log line that carries the JSON report. Shared so both sides spell it alike.</summary>
    public const string Directive = "##aetheus[migration-gate]";

    /// <summary>Ceiling on the migrations listed, so one report stays one bounded log line.</summary>
    public const int MaxMigrations = 100;

    /// <summary>Ceiling on the violations kept per migration.</summary>
    public const int MaxViolationsPerMigration = 20;

    /// <summary>Ceiling on the length of one violation or of <see cref="Detail"/>.</summary>
    public const int MaxTextLength = 500;

    public MigrationGateOutcome Outcome { get; init; }

    /// <summary>Pending migrations the gate found (all of them, even past <see cref="MaxMigrations"/>).</summary>
    public int PendingCount { get; init; }

    /// <summary>Why the gate could not run, for <see cref="MigrationGateOutcome.Unavailable"/>; null otherwise.</summary>
    public string? Detail { get; init; }

    /// <summary>One entry per inspected pending migration, in EF order. Empty when nothing was inspected.</summary>
    public IReadOnlyList<MigrationGateMigrationVerdict> Migrations { get; init; } = [];
}

/// <summary>What the gate found in one pending migration.</summary>
public sealed record MigrationGateMigrationVerdict
{
    public string MigrationId { get; init; } = string.Empty;

    /// <summary>True when the migration is expand-compatible (no violation).</summary>
    public bool Compatible { get; init; }

    public IReadOnlyList<string> Violations { get; init; } = [];
}

/// <summary>One gate verdict recorded on a run, as the run page shows it.</summary>
public sealed record PipelineRunMigrationGateDto
{
    public int Id { get; init; }
    public int RunId { get; init; }
    public string? StageName { get; init; }
    public string? StepName { get; init; }
    public MigrationGateOutcome Outcome { get; init; }
    public int PendingCount { get; init; }
    public string? Detail { get; init; }
    public DateTime RecordedAt { get; init; }
    public IReadOnlyList<MigrationGateMigrationVerdict> Migrations { get; init; } = [];
}
