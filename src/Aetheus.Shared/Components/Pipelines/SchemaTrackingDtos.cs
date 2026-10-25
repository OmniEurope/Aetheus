// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json.Serialization;

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>Whether a migration only adds to the schema (expand) or removes from it (contract).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SchemaMigrationKind>))]
public enum SchemaMigrationKind
{
    Expand = 0,
    Contract = 1
}

/// <summary>
/// The step of a two-release contraction (the previous colour still maps what is being retired):
/// <see cref="Unmap"/> removes the property from the entity behind a database default, <see cref="Drop"/>
/// drops the column in the release after.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SchemaContractionStep>))]
public enum SchemaContractionStep
{
    Unmap = 1,
    Drop = 2
}

/// <summary>One migration a run's migrate step inspected, classified for the run page's Schema tab.</summary>
public sealed record RunSchemaMigrationDto
{
    public string MigrationId { get; init; } = string.Empty;
    public SchemaMigrationKind Kind { get; init; }

    /// <summary>True when the gate let the migration run; false when the step stopped on it.</summary>
    public bool Applied { get; init; }

    public IReadOnlyList<string> Violations { get; init; } = [];
}

/// <summary>A contraction that is written in the expand/contract plan but not yet shipped.</summary>
public sealed record PendingContractionDto
{
    public string Id { get; init; } = string.Empty;
    public string Table { get; init; } = string.Empty;

    /// <summary>The columns this step retires from the entity or drops from the table.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    public SchemaContractionStep Step { get; init; }

    /// <summary>The migration that added what replaces the retired columns.</summary>
    public string ExpandMigration { get; init; } = string.Empty;

    /// <summary>
    /// The migration whose release must already be live before this step can ship; null when the step waits
    /// for the earlier step of the same contraction instead.
    /// </summary>
    public string? RequiredAfterMigration { get; init; }

    /// <summary>The release that first applied <see cref="RequiredAfterMigration"/>, when a deployment recorded it.</summary>
    public string? AppliedInRelease { get; init; }

    /// <summary>The first release this step can ship in (the one after <see cref="AppliedInRelease"/>); null when unknown.</summary>
    public string? PossibleFromRelease { get; init; }
}

/// <summary>What a run's Schema tab shows: the gate verdicts, the migrations they inspected and the contractions still due.</summary>
public sealed record PipelineRunSchemaDto
{
    public int RunId { get; init; }
    public IReadOnlyList<PipelineRunMigrationGateDto> Gates { get; init; } = [];
    public IReadOnlyList<RunSchemaMigrationDto> Migrations { get; init; } = [];
    public IReadOnlyList<PendingContractionDto> PendingContractions { get; init; } = [];
}

/// <summary>What an environment page shows of the schema tracking: the contractions still due and the last gate verdict.</summary>
public sealed record EnvironmentSchemaDto
{
    public int EnvironmentId { get; init; }
    public IReadOnlyList<PendingContractionDto> PendingContractions { get; init; } = [];

    /// <summary>The latest verdict of a migrate step that ran on one of the environment's servers; null when none did.</summary>
    public PipelineRunMigrationGateDto? LastGate { get; init; }
}
