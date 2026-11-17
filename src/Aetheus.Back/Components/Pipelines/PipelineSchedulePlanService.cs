// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Reads the plan from the pipeline YAML mirror (nothing about a schedule is stored apart). The
/// engine's own rule decides what is live: only <see cref="PipelineTriggerType.Schedule"/> fires. A
/// disabled task is therefore listed without occurrences or next run, so it never counts as an overlap.
/// Overlaps are computed among the tasks the caller may read: a pipeline it cannot read stays invisible,
/// including through the overlap minutes of the others.
/// </summary>
internal sealed class PipelineSchedulePlanService(
    IPipelineScheduleRepository repository,
    ServerActionPlanReader serverActionReader,
    IPipelineGitService pipelineGit,
    TimeProvider clock) : IPipelineSchedulePlanService
{
    /// <summary>One pipeline of the plan with the schedule that is drawn: the one of its mirrored definition, or of the
    /// branch the reader chose (recette R-5). <see cref="ChosenMissing"/>: the chosen branch has no readable definition, the
    /// own one is drawn.</summary>
    private sealed record Candidate(PipelineScheduleRow Row, string? Cron, bool Enabled, string? Branch, bool Chosen, bool ChosenMissing);

    private static readonly DateTime EarliestUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LatestUtc = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task<SchedulePlanDto> GetPlanAsync(
        int organizationId, DateTime from, DateTime to, IReadOnlyCollection<int>? accessiblePipelineIds,
        ServerActionScope serverActions, IReadOnlyDictionary<int, string>? sourceBranches, CancellationToken ct)
    {
        var fromUtc = ScheduleOccurrenceExpander.AsUtc(from);
        var toUtc = ScheduleOccurrenceExpander.AsUtc(to);
        Validate(organizationId, fromUtc, toUtc);
        var plan = new SchedulePlanDto { OrganizationId = organizationId, FromUtc = fromUtc, ToUtc = toUtc };
        var actions = await serverActionReader.ReadAsync(organizationId, serverActions, fromUtc, toUtc, ct).ConfigureAwait(false);
        if (accessiblePipelineIds is { Count: 0 } && actions.Count == 0)
        {
            return plan;
        }

        var rows = accessiblePipelineIds is { Count: 0 }
            ? []
            : await repository.GetCandidatesAsync(organizationId, accessiblePipelineIds, ct).ConfigureAwait(false);
        var scheduled = await ReadCandidatesAsync(rows, sourceBranches, ct).ConfigureAwait(false);
        var averages = await repository
            .GetAverageMinutesAsync([.. scheduled.Select(item => item.Row.Id)], ct).ConfigureAwait(false);
        var lastRuns = await repository
            .GetLastRunsAsync([.. scheduled.Select(item => item.Row.Id)], ct).ConfigureAwait(false);
        var capacity = await repository.GetExecutionCapacityAsync(organizationId, ct).ConfigureAwait(false);
        var now = clock.GetUtcNow().UtcDateTime;
        var tasks = scheduled
            .Select(item => BuildTask(item.Row, item.Cron, averages, fromUtc, toUtc, now, item) with { LastRun = lastRuns.GetValueOrDefault(item.Row.Id) })
            .ToList();
        // R-8: the CVE watch uses no runner of the organization, it is listed beside the others but counted in no overlap.
        var counted = actions.Where(action => ServerActionDefaults.CountsInOverlaps(action.Kind)).ToList();
        var (filledTasks, filledActions) = ScheduleOverlapFinder.Fill(tasks, counted, capacity);
        return plan with
        {
            ExecutionCapacity = capacity,
            Tasks = filledTasks,
            ServerActions = [.. filledActions, .. actions.Where(action => !ServerActionDefaults.CountsInOverlaps(action.Kind))]
        };
    }

    public async Task<PipelineScheduleTarget?> FindTargetAsync(int pipelineId, CancellationToken ct)
    {
        var row = await repository.GetTargetAsync(pipelineId, ct).ConfigureAwait(false);
        return row is null
            ? null
            : new PipelineScheduleTarget(row.Id, row.Name, row.YamlDefinition, row.OrganizationId > 0 ? row.OrganizationId : null);
    }

    public async Task<SchedulePreviewDto> PreviewAsync(
        PipelineScheduleTarget target, ScheduleFrequencySpec proposal, DateTime from, DateTime to,
        IReadOnlyCollection<int>? accessiblePipelineIds, CancellationToken ct)
    {
        var fromUtc = ScheduleOccurrenceExpander.AsUtc(from);
        var toUtc = ScheduleOccurrenceExpander.AsUtc(to);
        ValidateWindow(fromUtc, toUtc);
        var cron = ScheduleFrequencyCodec.Encode(proposal);
        if (cron is null || !ScheduleOccurrenceExpander.IsFiredByTheEngine(cron))
        {
            throw new BadRequestException("This frequency and time cannot be written as a schedule.");
        }

        var others = await GetOtherEnabledAsync(target, accessiblePipelineIds, ct).ConfigureAwait(false);
        var averages = await repository
            .GetAverageMinutesAsync([target.PipelineId, .. others.Select(item => item.Row.Id)], ct).ConfigureAwait(false);
        var now = clock.GetUtcNow().UtcDateTime;
        // The proposal replaces the pipeline's present schedule: it is built as an enabled task of its own.
        var proposed = BuildTask(
            new PipelineScheduleRow(target.PipelineId, target.Name, PipelineTriggerType.Schedule, target.YamlDefinition),
            cron, averages, fromUtc, toUtc, now);
        var otherTasks = others.Select(item => BuildTask(item.Row, item.Cron, averages, fromUtc, toUtc, now)).ToList();
        // The same capacity as the plan: runs side by side within it are not an overlap.
        var capacity = target.OrganizationId is { } organizationId
            ? await repository.GetExecutionCapacityAsync(organizationId, ct).ConfigureAwait(false)
            : 1;
        var filled = ScheduleOverlapFinder.Fill([proposed, .. otherTasks], capacity)[0];
        return new SchedulePreviewDto
        {
            PipelineId = target.PipelineId,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Proposal = filled,
            Overlaps = OverlapsOf(proposed, otherTasks, filled),
            YamlError = PipelineScheduleChange.PrepareExpression(target.YamlDefinition, cron, enabled: true).Error
        };
    }

    // The tasks the engine fires, among those the caller may read, without the previewed pipeline.
    private async Task<List<(PipelineScheduleRow Row, string? Cron)>> GetOtherEnabledAsync(
        PipelineScheduleTarget target, IReadOnlyCollection<int>? accessiblePipelineIds, CancellationToken ct)
    {
        if (target.OrganizationId is not { } organizationId || accessiblePipelineIds is { Count: 0 })
        {
            return [];
        }

        var rows = await repository.GetCandidatesAsync(organizationId, accessiblePipelineIds, ct).ConfigureAwait(false);
        return rows
            .Where(row => row.TriggerType == PipelineTriggerType.Schedule && row.Id != target.PipelineId)
            .Select(row => (Row: row, Cron: PipelineScheduleReader.Read(row.YamlDefinition)?.Schedule))
            .ToList();
    }

    // One pair at a time, so that a pipeline listed overlaps the proposal itself and not a third task. Only the
    // proposal's runs that exceed the capacity (the ones the filled proposal flags) count: with room to spare for
    // both, two runs side by side are not listed.
    private static List<ScheduleOverlapDto> OverlapsOf(ScheduledTaskDto proposed, List<ScheduledTaskDto> others, ScheduledTaskDto filledProposal)
    {
        var overCapacity = filledProposal.Occurrences.Where(occurrence => occurrence.OverlapMinutes > 0).Select(occurrence => occurrence.StartUtc).ToHashSet();
        return
        [
        .. others
            .Select(other => (Other: other, Runs: ScheduleOverlapFinder.Fill([proposed, other])[0].Occurrences
                .Where(occurrence => occurrence.OverlapMinutes > 0 && overCapacity.Contains(occurrence.StartUtc)).ToList()))
            .Where(pair => pair.Runs.Count > 0)
            .Select(pair => new ScheduleOverlapDto
            {
                PipelineId = pair.Other.PipelineId,
                PipelineName = pair.Other.PipelineName,
                OverlapMinutes = pair.Runs.Sum(occurrence => occurrence.OverlapMinutes),
                OverlappingRuns = pair.Runs.Count
            })
            .OrderByDescending(overlap => overlap.OverlapMinutes)
            .ThenBy(overlap => overlap.PipelineName, StringComparer.Ordinal)
            .ThenBy(overlap => overlap.PipelineId)
        ];
    }

    private static void Validate(int organizationId, DateTime fromUtc, DateTime toUtc)
    {
        if (organizationId <= 0)
        {
            throw new BadRequestException("The organization is required.");
        }

        ValidateWindow(fromUtc, toUtc);
    }

    private static void ValidateWindow(DateTime fromUtc, DateTime toUtc)
    {
        if (fromUtc >= toUtc)
        {
            throw new BadRequestException("The start of the window must be before its end.");
        }

        if (fromUtc < EarliestUtc || toUtc > LatestUtc)
        {
            throw new BadRequestException(
                $"The window must stay between {EarliestUtc.Year} and {LatestUtc.Year}.");
        }

        if (toUtc - fromUtc > TimeSpan.FromDays(ScheduleDefaults.MaxWindowDays))
        {
            throw new BadRequestException($"The window cannot exceed {ScheduleDefaults.MaxWindowDays} days.");
        }
    }

    /// <summary>
    /// The pipelines of the plan with the schedule drawn for each. The mirrored definition tells it, unless the reader chose a
    /// branch for the pipeline (recette R-5): its definition is then read from the project's repository on that branch, as the
    /// pipeline's own source branch is read, and its <c>trigger:</c> and <c>schedule:</c> decide. The choice is never stored.
    /// A choice on a pipeline that has no repository to read, or a branch with no readable definition of it, leaves the
    /// pipeline's own schedule drawn and says so (<see cref="ScheduledTaskDto.ChosenBranchMissing"/>). Scheduled for the engine
    /// stays listed even with an unreadable YAML; a manual pipeline with no schedule value is not part of the plan, unless its
    /// card carries a choice: it stays, so the reader can choose back.
    /// </summary>
    private async Task<List<Candidate>> ReadCandidatesAsync(
        List<PipelineScheduleRow> rows, IReadOnlyDictionary<int, string>? sourceBranches, CancellationToken ct)
    {
        List<Candidate> candidates = [];
        foreach (var row in rows)
        {
            var own = PipelineScheduleReader.Read(row.YamlDefinition)?.Schedule;
            var ownEnabled = row.TriggerType == PipelineTriggerType.Schedule;
            var candidate = new Candidate(row, own, ownEnabled, row.SourceBranch, false, false);
            if (sourceBranches is not null && sourceBranches.TryGetValue(row.Id, out var branch)
                && !string.Equals(branch, row.SourceBranch, StringComparison.Ordinal))
            {
                var yaml = row.ProjectId is { } projectId
                    ? await pipelineGit.ReadProjectPipelineYamlAsync(projectId, row.Name, ct, branch, row.SourceRepositoryId).ConfigureAwait(false)
                    : null;
                var info = PipelineScheduleReader.Read(yaml);
                candidate = info is null
                    ? candidate with { Branch = branch, ChosenMissing = true }
                    : candidate with { Cron = info.Schedule, Enabled = info.IsScheduled, Branch = branch, Chosen = true };
            }

            if (candidate.Enabled || candidate.Cron is not null || candidate.Chosen || candidate.ChosenMissing)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static ScheduledTaskDto BuildTask(
        PipelineScheduleRow row, string? cron, Dictionary<int, int> averages, DateTime fromUtc, DateTime toUtc, DateTime now,
        Candidate? candidate = null)
    {
        var enabled = candidate?.Enabled ?? row.TriggerType == PipelineTriggerType.Schedule;
        var isPlanned = !averages.TryGetValue(row.Id, out var average);
        var durationMinutes = isPlanned ? ScheduleDefaults.PlannedMinutes : average;
        var expansion = enabled
            ? ScheduleOccurrenceExpander.ExpandBounded(cron, fromUtc, toUtc, durationMinutes)
            : new ScheduleExpansion([], false);
        var frequency = ScheduleFrequencyCodec.Decode(cron);
        return new ScheduledTaskDto
        {
            PipelineId = row.Id,
            PipelineName = row.Name,
            Enabled = enabled,
            CronExpression = cron ?? string.Empty,
            Frequency = frequency.Frequency,
            Day = frequency.Day,
            Hour = frequency.Hour,
            Minute = frequency.Minute,
            DurationMinutes = durationMinutes,
            IsPlanned = isPlanned,
            NextRunUtc = enabled ? ScheduleOccurrenceExpander.NextOccurrence(cron, now) : null,
            Occurrences = expansion.Occurrences,
            OccurrencesTruncated = expansion.IsTruncated,
            SourceBranch = candidate?.Branch ?? row.SourceBranch,
            OwnSourceBranch = row.SourceBranch,
            CanChooseSourceBranch = row.ProjectId is not null,
            ChosenBranchMissing = candidate?.ChosenMissing ?? false
        };
    }
}
