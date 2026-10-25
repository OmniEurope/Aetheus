// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The expand/contract tracking of the database as the run page and the environment page show it: the
/// migrations a run's gate inspected, classified expand or contract, and the contractions still due with the
/// release from which each can ship.
/// </summary>
public interface IPipelineSchemaTrackingService
{
    Task<PipelineRunSchemaDto> GetForRunAsync(int runId, CancellationToken ct = default);

    /// <summary>Null when the environment does not exist.</summary>
    Task<EnvironmentSchemaDto?> GetForEnvironmentAsync(int environmentId, CancellationToken ct = default);
}

public sealed class PipelineSchemaTrackingService(IPipelineMigrationGateRepository repo) : IPipelineSchemaTrackingService
{
    public async Task<PipelineRunSchemaDto> GetForRunAsync(int runId, CancellationToken ct = default)
    {
        var gates = (await repo.ListForRunAsync(runId, ct).ConfigureAwait(false))
            .Select(PipelineMigrationGateService.ToDto)
            .ToList();
        return new PipelineRunSchemaDto
        {
            RunId = runId,
            Gates = gates,
            Migrations = Classify(gates),
            // The run page shows the Schema tab only for a run with a gate record: dating each pending contraction
            // costs several queries, which a run without one would pay on every load for nothing.
            PendingContractions = gates.Count == 0 ? [] : await ListPendingAsync(ct).ConfigureAwait(false)
        };
    }

    public async Task<EnvironmentSchemaDto?> GetForEnvironmentAsync(int environmentId, CancellationToken ct = default)
    {
        if (!await repo.EnvironmentExistsAsync(environmentId, ct).ConfigureAwait(false)) return null;
        var last = await repo.FindLatestForEnvironmentAsync(environmentId, ct).ConfigureAwait(false);
        return new EnvironmentSchemaDto
        {
            EnvironmentId = environmentId,
            PendingContractions = await ListPendingAsync(ct).ConfigureAwait(false),
            LastGate = last is null ? null : PipelineMigrationGateService.ToDto(last)
        };
    }

    /// <summary>
    /// A migration is a contract when the gate found a violation in it or when it is a reviewed contract drop.
    /// The gate refuses EF drops but lets reviewed raw SQL through, so the registry completes what the verdict says.
    /// A migration inspected twice keeps its latest verdict.
    /// </summary>
    private static List<RunSchemaMigrationDto> Classify(IReadOnlyList<PipelineRunMigrationGateDto> gates)
    {
        var latest = new Dictionary<string, RunSchemaMigrationDto>(StringComparer.Ordinal);
        foreach (var gate in gates)
        {
            var applied = gate.Outcome is MigrationGateOutcome.Compatible or MigrationGateOutcome.InitialCreation;
            foreach (var migration in gate.Migrations)
            {
                latest[migration.MigrationId] = new RunSchemaMigrationDto
                {
                    MigrationId = migration.MigrationId,
                    Kind = !migration.Compatible || SchemaContractionRegistry.IsContractMigration(migration.MigrationId)
                        ? SchemaMigrationKind.Contract
                        : SchemaMigrationKind.Expand,
                    Applied = applied,
                    Violations = migration.Violations
                };
            }
        }
        return [.. latest.Values];
    }

    private async Task<List<PendingContractionDto>> ListPendingAsync(CancellationToken ct)
    {
        var pending = new List<PendingContractionDto>(SchemaContractionRegistry.Pending.Count);
        foreach (var step in SchemaContractionRegistry.Pending)
        {
            var applied = step.RequiredAfterMigration is null
                ? null
                : await repo.FindFirstAppliedReleaseVersionAsync(step.RequiredAfterMigration, ct).ConfigureAwait(false);
            pending.Add(new PendingContractionDto
            {
                Id = step.Id,
                Table = step.Table,
                Columns = step.Columns,
                Step = step.Step,
                ExpandMigration = step.ExpandMigration,
                RequiredAfterMigration = step.RequiredAfterMigration,
                AppliedInRelease = applied,
                PossibleFromRelease = applied is null ? null : NextRelease(applied)
            });
        }
        return pending;
    }

    /// <summary>
    /// The release after <paramref name="version"/> (<c>major.minor.number</c>: the number counts per series);
    /// null when the version does not end in a number, rather than guessing.
    /// </summary>
    internal static string? NextRelease(string version)
    {
        var cut = version.LastIndexOf('.');
        return cut >= 0 && int.TryParse(version.AsSpan(cut + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? string.Concat(version.AsSpan(0, cut + 1), (number + 1).ToString(CultureInfo.InvariantCulture))
            : null;
    }
}
