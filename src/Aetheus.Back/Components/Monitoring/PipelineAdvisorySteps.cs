// SPDX-License-Identifier: EUPL-1.2
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Components.Monitoring;

/// <summary>
/// Which steps of a run are advisory: the <c>type: ai</c> steps declared <c>advisory: true</c> in the
/// run's YAML snapshot. Read from the snapshot rather than persisted on the step, since the snapshot is
/// immutable per run. Lives in this read-model module so the run detail, the run status and every run
/// LIST (dashboard tile, recent, active and paged runs) answer with the same rule; the list projection
/// cannot read the snapshot in SQL, so <see cref="ApplyToListAsync"/> completes it afterwards.
/// </summary>
public static class PipelineAdvisorySteps
{
    // Only a snapshot that names the key can declare an advisory step: the database filters on it, so
    // a list page parses no YAML unless one of its runs can actually carry an advisory step.
    private const string AdvisoryYamlKey = "advisory";

    /// <summary>The advisory AI steps of a definition, as stage/step keys.</summary>
    public static HashSet<string> Keys(PipelineYamlDefinition definition) =>
        [.. YamlParsingHelper.FlattenJobs(definition)
            .SelectMany(stage => stage.Steps
                .Where(step => step.Advisory && string.Equals(step.Type, "ai", StringComparison.OrdinalIgnoreCase))
                .Select(step => Key(stage.Name, step.Name)))];

    /// <summary>True when the step (stage + name, matrix leg suffix ignored) is in <see cref="Keys"/>.</summary>
    public static bool IsAdvisory(IReadOnlySet<string> advisoryKeys, string stageName, string stepName, string? matrixLeg) =>
        advisoryKeys.Contains(Key(
            stageName, matrixLeg is null ? stepName : stepName.Replace($" [{matrixLeg}]", string.Empty)));

    /// <summary>The run's own definition snapshot, null when missing or unparseable.</summary>
    public static PipelineYamlDefinition? ParseSnapshot(string? yamlSnapshot)
    {
        if (string.IsNullOrWhiteSpace(yamlSnapshot)) return null;
        try
        {
            return YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yamlSnapshot);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sets <see cref="PipelineStepRunDto.IsAdvisory"/> on list-projected runs. Advisory steps always
    /// carry <c>ContinueOnError</c> (see <c>PipelineStepRunPlan</c>), so only runs with such a step are
    /// read, and only their snapshot column.
    /// </summary>
    public static async Task<List<PipelineRunDto>> ApplyToListAsync(
        AppDbContext db, List<PipelineRunDto> runs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(runs);
        var candidateIds = runs.Where(run => run.Steps.Any(step => step.ContinueOnError))
            .Select(run => run.Id).ToList();
        if (candidateIds.Count == 0) return runs;

        var snapshots = await db.PipelineRuns.AsNoTracking()
            .Where(run => candidateIds.Contains(run.Id)
                && run.YamlSnapshot != null && run.YamlSnapshot.Contains(AdvisoryYamlKey))
            .Select(run => new { run.Id, run.YamlSnapshot })
            .ToListAsync(ct).ConfigureAwait(false);
        var keysByRun = new Dictionary<int, HashSet<string>>();
        foreach (var snapshot in snapshots)
        {
            var definition = ParseSnapshot(snapshot.YamlSnapshot);
            if (definition is null) continue;
            var keys = Keys(definition);
            if (keys.Count > 0) keysByRun[snapshot.Id] = keys;
        }
        if (keysByRun.Count == 0) return runs;

        return runs.Select(run => keysByRun.TryGetValue(run.Id, out var keys)
            ? run with
            {
                Steps = run.Steps.Select(step => step with
                {
                    IsAdvisory = IsAdvisory(keys, step.StageName, step.StepName, step.MatrixLeg)
                }).ToList()
            }
            : run).ToList();
    }

    private static string Key(string stageName, string stepName) => $"{stageName}\u001f{stepName}";
}
