// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The expand/contract gate verdicts of a run, as its page shows them (recette R1-43).</summary>
public interface IPipelineMigrationGateService
{
    Task<List<PipelineRunMigrationGateDto>> GetForRunAsync(int runId, CancellationToken ct = default);
}

public sealed class PipelineMigrationGateService(IPipelineMigrationGateRepository repo) : IPipelineMigrationGateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<List<PipelineRunMigrationGateDto>> GetForRunAsync(int runId, CancellationToken ct = default)
    {
        var rows = await repo.ListForRunAsync(runId, ct).ConfigureAwait(false);
        return [.. rows.Select(ToDto)];
    }

    internal static PipelineRunMigrationGateDto ToDto(PipelineMigrationGateVerdict row) => new()
    {
        Id = row.Id,
        RunId = row.PipelineRunId,
        StageName = row.StageName,
        StepName = row.StepName,
        Outcome = row.Outcome,
        PendingCount = row.PendingCount,
        Detail = row.Detail,
        RecordedAt = row.CreatedAt,
        // Written by TaskMigrationGateParser from an already validated report.
        Migrations = string.IsNullOrWhiteSpace(row.MigrationsJson)
            ? []
            : JsonSerializer.Deserialize<List<MigrationGateMigrationVerdict>>(row.MigrationsJson, JsonOptions) ?? []
    };
}
