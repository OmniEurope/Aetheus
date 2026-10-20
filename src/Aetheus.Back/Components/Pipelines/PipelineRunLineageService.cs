// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;

namespace Aetheus.Back.Components.Pipelines;

public interface IPipelineRunLineageService
{
    /// <summary>Who launched the run and which runs it started; null for an unknown run.</summary>
    Task<PipelineRunLineageDto?> GetLineageAsync(int runId, CancellationToken ct = default);
}

/// <summary> The lineage tile of a run's page. Read on its own, once, when the page opens: the run
/// itself is refreshed at every step change and does not pay for these two lookups. </summary>
public sealed class PipelineRunLineageService(
    IPipelineRunLineageReader lineage,
    IAuditService audit,
    TimeProvider timeProvider) : IPipelineRunLineageService
{
    /// <summary>The launch writes its audit entry once the first tasks exist; both are bounded by this.</summary>
    internal static readonly TimeSpan LaunchSlack = TimeSpan.FromMinutes(10);

    /// <summary>The audit's name for "no signed-in caller": a schedule, a push, another run.</summary>
    internal const string SystemActor = "system";

    public async Task<PipelineRunLineageDto?> GetLineageAsync(int runId, CancellationToken ct = default)
    {
        var window = await lineage.GetRunWindowAsync(runId, ct).ConfigureAwait(false);
        if (window is null) return null;

        var until = (window.CompletedAt ?? timeProvider.GetUtcNow().UtcDateTime) + LaunchSlack;
        var downstream = await lineage.GetDownstreamRunsAsync(runId, window.StartedAt, until, ct).ConfigureAwait(false);
        // The launcher audits every run it starts ("Triggered", PipelineRun, run id) under the caller's
        // name; the entry is the only record of who pressed the button.
        var launches = await audit.GetLogsPagedAsync(
            1, 1, action: "Triggered", entityType: "PipelineRun", entityId: runId,
            dateFrom: window.StartedAt - LaunchSlack, dateTo: window.StartedAt + LaunchSlack, ct: ct).ConfigureAwait(false);
        var actor = launches.Items.FirstOrDefault()?.Username;
        return new PipelineRunLineageDto
        {
            TriggeredBy = string.IsNullOrWhiteSpace(actor) || string.Equals(actor, SystemActor, StringComparison.OrdinalIgnoreCase)
                ? null
                : actor,
            Downstream = downstream
        };
    }
}
