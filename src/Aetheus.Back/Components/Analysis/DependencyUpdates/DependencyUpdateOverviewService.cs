// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.DependencyUpdates;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>What the Quality page and the settings of a project read and write of the automatic dependency updates (PLAN-007).</summary>
public interface IDependencyUpdateOverviewService
{
    /// <summary>The setting and the latest updates with the live status of their runs; null when the project does not exist.</summary>
    Task<DependencyUpdatesOverviewDto?> GetAsync(int projectId, CancellationToken ct = default);

    /// <summary>Turns the updates of a project on or off. False when the project does not exist.</summary>
    Task<bool> SetEnabledAsync(int projectId, bool enabled, CancellationToken ct = default);
}

internal sealed class DependencyUpdateOverviewService(DependencyUpdateRepository repository, IAuditService audit, TimeProvider timeProvider)
    : IDependencyUpdateOverviewService
{
    private const int Shown = 50;

    public async Task<DependencyUpdatesOverviewDto?> GetAsync(int projectId, CancellationToken ct = default)
    {
        if (await repository.GetEnabledAsync(projectId, ct).ConfigureAwait(false) is not { } enabled) return null;
        var updates = await repository.GetOfProjectAsync(projectId, Shown, ct).ConfigureAwait(false);
        var pipelineIds = updates.SelectMany(update => new[] { update.CiPipelineId, update.QaPipelineId })
            .OfType<int>().Distinct().ToList();
        var names = pipelineIds.Count == 0 ? new Dictionary<int, string>() : await repository.GetPipelineNamesAsync(pipelineIds, ct).ConfigureAwait(false);
        var latest = await LatestRunsAsync(updates, ct).ConfigureAwait(false);
        return new DependencyUpdatesOverviewDto { Enabled = enabled, Items = [.. updates.Select(update => ToDto(update, names, latest))] };
    }

    public async Task<bool> SetEnabledAsync(int projectId, bool enabled, CancellationToken ct = default)
    {
        if (!await repository.SetEnabledAsync(projectId, enabled, timeProvider.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false)) return false;
        await audit.LogAsync(enabled ? "EnabledDependencyUpdates" : "DisabledDependencyUpdates", "Project", projectId, null, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>The run each pipeline has on the branch and commit of each update: the latest wins, so a run a push superseded is replaced by
    /// the one that took its place. The run the update launched stands when no other is found.</summary>
    private Task<Dictionary<(int PipelineId, string Branch), DependencyUpdateRunRow>> LatestRunsAsync(
        IReadOnlyList<DependencyUpdate> updates, CancellationToken ct)
    {
        // One query for the whole page, not one per row and pipeline (up to 100).
        var wanted = (from update in updates.Where(update => update.Branch is not null && update.CommitSha is not null)
                      from pipelineId in new[] { update.CiPipelineId, update.QaPipelineId }.OfType<int>()
                      select (pipelineId, update.Branch!, update.CommitSha!)).ToList();
        return repository.FindLatestRunsAsync(wanted, ct);
    }

    private static DependencyUpdateDto ToDto(
        DependencyUpdate update, IReadOnlyDictionary<int, string> names, IReadOnlyDictionary<(int PipelineId, string Branch), DependencyUpdateRunRow> latest) => new()
        {
            Id = update.Id,
            Ecosystem = update.Ecosystem,
            PackageName = update.PackageName,
            FromVersion = update.FromVersion,
            TargetVersion = update.TargetVersion,
            Advisory = update.Advisory,
            IsMajorUpgrade = update.IsMajorUpgrade,
            Status = update.Status,
            Reason = update.Reason,
            License = update.License,
            LicenseOfTargetVersion = update.LicenseOfTargetVersion,
            Branch = update.Branch,
            RepositoryId = update.GitInternalRepoId,
            CommitSha = update.CommitSha,
            CreatedAt = update.CreatedAt,
            UpdatedAt = update.UpdatedAt,
            Runs = [.. Runs(update, names, latest)]
        };

    private static IEnumerable<DependencyUpdateRunDto> Runs(
        DependencyUpdate update, IReadOnlyDictionary<int, string> names, IReadOnlyDictionary<(int PipelineId, string Branch), DependencyUpdateRunRow> latest)
    {
        foreach (var (pipelineId, launched) in new[] { (update.CiPipelineId, update.CiPipelineRunId), (update.QaPipelineId, update.QaPipelineRunId) })
        {
            if (pipelineId is not { } id || !names.TryGetValue(id, out var name)) continue;
            var run = update.Branch is not null && latest.TryGetValue((id, update.Branch), out var found) ? found : null;
            yield return new DependencyUpdateRunDto { PipelineId = id, PipelineName = name, RunId = run?.Id ?? launched, Status = run?.Status };
        }
    }
}
