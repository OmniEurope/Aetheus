// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Tasks;

/// <summary>
/// Reads the <see cref="MigrationGateReport.Directive"/> lines a blue-green migrate step wrote
/// (recette R1-43). A line that is not a well-formed report is skipped rather than guessed at: the
/// human log lines stay the record of what happened, the row only adds a durable copy of the verdict.
/// </summary>
internal static class TaskMigrationGateParser
{
    /// <summary>One migrate step evaluates the gate once; a few more covers a retried evaluation.</summary>
    private const int MaxReportsPerTask = 8;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static List<PipelineMigrationGateVerdict> Parse(
        string? output, int pipelineRunId, string? stageName, string? stepName, DateTime createdAt)
    {
        var verdicts = new List<PipelineMigrationGateVerdict>();
        if (string.IsNullOrEmpty(output)) return verdicts;

        foreach (var rawLine in output.Split('\n'))
        {
            if (verdicts.Count == MaxReportsPerTask) break;
            var line = rawLine.TrimEnd('\r');
            var start = line.IndexOf(MigrationGateReport.Directive, StringComparison.Ordinal);
            if (start < 0) continue;
            if (Deserialize(line[(start + MigrationGateReport.Directive.Length)..]) is not { } report) continue;

            var migrations = report.Migrations
                .Where(migration => !string.IsNullOrWhiteSpace(migration.MigrationId))
                .Take(MigrationGateReport.MaxMigrations)
                .Select(migration => migration with
                {
                    MigrationId = Bound(migration.MigrationId),
                    Violations = [.. migration.Violations.Take(MigrationGateReport.MaxViolationsPerMigration).Select(Bound)]
                })
                .ToList();
            verdicts.Add(new PipelineMigrationGateVerdict
            {
                PipelineRunId = pipelineRunId,
                StageName = stageName,
                StepName = stepName,
                Outcome = report.Outcome,
                PendingCount = Math.Max(0, report.PendingCount),
                Detail = report.Detail is { Length: > 0 } detail ? Bound(detail) : null,
                MigrationsJson = JsonSerializer.Serialize(migrations, JsonOptions),
                CreatedAt = createdAt
            });
        }
        return verdicts;
    }

    private static MigrationGateReport? Deserialize(string json)
    {
        try
        {
            var report = JsonSerializer.Deserialize<MigrationGateReport>(json, JsonOptions);
            return report is not null && Enum.IsDefined(report.Outcome) ? report : null;
        }
        catch (JsonException)
        {
            // Any step can print this prefix; a malformed payload is not a verdict.
            return null;
        }
    }

    private static string Bound(string text) =>
        text.Length <= MigrationGateReport.MaxTextLength ? text : text[..MigrationGateReport.MaxTextLength];
}
