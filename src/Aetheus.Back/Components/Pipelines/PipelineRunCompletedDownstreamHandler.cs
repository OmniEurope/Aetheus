// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines.Events;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.DomainEvents;
using YamlDotNet.Core;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Pipeline chaining: when an upstream run SUCCEEDS, trigger each pipeline declared under the upstream
/// YAML's <c>on_success:</c> list, forwarding the upstream context (and optional release selector) as
/// variables so a downstream <c>type: deploy</c> step can deploy the produced release. Resolution is
/// project-scoped; a target that cannot be started is skipped without failing the upstream run, and the
/// exact reason is written on the upstream run (a warning its page shows) and notified to the project's
/// subscribers (recette R-48), not only logged.
/// </summary>
public sealed class PipelineRunCompletedDownstreamHandler(
    IPipelineRepository repo,
    IPipelineRunService runService,
    PipelineRunNotificationPublisher notifications,
    ILogger<PipelineRunCompletedDownstreamHandler> logger)
    : IDomainEventHandler<PipelineRunCompletedEvent>
{
    /// <summary>Selector accepted on an <c>on_success.release</c> entry meaning "the release this same
    /// run just published" (see the F3 flow where <c>aetheus-candidate</c> chains into
    /// <c>aetheus-deploy-prod</c> without hand-typing the version). Resolved here, not left as the
    /// literal string, because downstream <c>candidateVersion</c> derivation needs a real version.</summary>
    private const string LatestReleaseSelector = "latest";

    public async Task HandleAsync(PipelineRunCompletedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.Status != PipelineStatus.Success) return;

        var run = await repo.GetPipelineRunWithPipelineAsync(domainEvent.PipelineRunId, ct).ConfigureAwait(false);
        if (run?.Pipeline is null) return;
        var (def, parseError) = ParseDefinition(run, domainEvent.PipelineRunId);
        if (parseError is not null)
        {
            await ReportRefusalAsync(run, null, null, null,
                PipelineProblem.Of(
                    PipelineProblemCodes.DownstreamSnapshotUnparsable,
                    $"the upstream run's YAML snapshot cannot be parsed, so its on_success list is unknown: {parseError}",
                    ("detail", parseError)), ct)
                .ConfigureAwait(false);
            return;
        }
        if (def?.OnSuccess is not { Count: > 0 }) return;

        var projectId = await repo.GetPipelineProjectIdAsync(run.Pipeline, ct).ConfigureAwait(false);
        if (projectId is not { } pid) // downstream resolution is project-scoped
        {
            await ReportRefusalAsync(run, null, null, null,
                PipelineProblem.Of(
                    PipelineProblemCodes.DownstreamNoProject,
                    "the upstream pipeline belongs to no project, and on_success targets are resolved inside a project"), ct)
                .ConfigureAwait(false);
            return;
        }

        // The ancestor chain travels in UPSTREAM_CHAIN (comma-separated pipeline ids). Append this run's
        // pipeline so the next hop can detect A→B→A cycles and a runaway depth - not just immediate A→A.
        var ancestorChain = PipelineUpstreamChain.Read(run.AdditionalVariablesJson);
        var chain = new List<int>(ancestorChain) { run.PipelineId };

        foreach (var trigger in def.OnSuccess)
            await TriggerDownstreamAsync(trigger, run, pid, chain, ct).ConfigureAwait(false);
    }

    private (PipelineYamlDefinition? Definition, string? Error) ParseDefinition(PipelineRun run, int runId)
    {
        var yaml = !string.IsNullOrWhiteSpace(run.YamlSnapshot) ? run.YamlSnapshot : run.Pipeline!.YamlDefinition;
        try
        {
            return (YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(yaml), null);
        }
        catch (YamlException ex)
        {
            logger.LogWarning(ex, "Downstream chaining skipped for run {RunId}: run YAML snapshot failed to parse", runId);
            return (null, ex.Message);
        }
    }

    private async Task TriggerDownstreamAsync(
        PipelineDownstreamTrigger trigger, PipelineRun run, int projectId,
        IReadOnlyCollection<int> chain, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trigger.Pipeline)) return;
        var targetName = trigger.Pipeline.Trim();
        var target = await repo.FindPipelineByNameAndProjectAsync(targetName, projectId, ct).ConfigureAwait(false);
        if (target is null)
        {
            await ReportRefusalAsync(run, projectId, targetName, null,
                PipelineProblem.Of(
                    PipelineProblemCodes.DownstreamTargetMissing,
                    $"no pipeline named '{targetName}' exists in project {projectId}",
                    ("pipeline", targetName), ("project", projectId)), ct).ConfigureAwait(false);
            return;
        }
        if (chain.Contains(target.Id))
        {
            await ReportRefusalAsync(run, projectId, targetName, target.Id,
                PipelineProblem.Of(
                    PipelineProblemCodes.DownstreamCycle,
                    $"pipeline {target.Id} is already in the upstream chain [{string.Join(",", chain)}], cycle refused",
                    ("pipeline", target.Id), ("upstream", string.Join(",", chain))), ct)
                .ConfigureAwait(false);
            return;
        }
        if (chain.Count >= MaxChainDepth)
        {
            await ReportRefusalAsync(run, projectId, targetName, target.Id,
                PipelineProblem.Of(
                    PipelineProblemCodes.DownstreamDepthLimit,
                    $"the upstream chain depth {chain.Count} reached the limit {MaxChainDepth}",
                    ("depth", chain.Count), ("limit", MaxChainDepth)), ct).ConfigureAwait(false);
            return;
        }

        var vars = await BuildDownstreamVariablesAsync(trigger, run, chain, ct).ConfigureAwait(false);
        PipelineChainedRunOutcome outcome;
        try
        {
            outcome = await runService.TriggerChainedRunAsync(target.Id, vars, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mandatory catch: the launch checks (preflight, conflicts) refuse by throwing, and that is the
            // reason to show on the upstream run. It also keeps the next on_success target going.
            await ReportRefusalAsync(
                run, projectId, targetName, target.Id,
                PipelineProblem.Of(
                    PipelineProblemCodes.DownstreamLaunchRefused, $"the launch was refused: {ex.Message}", ("detail", ex.Message)),
                ct, (ex as BadRequestException)?.Problems).ConfigureAwait(false);
            return;
        }
        if (outcome.Run is null)
        {
            await ReportRefusalAsync(
                run, projectId, targetName, target.Id,
                outcome.Problem ?? PipelineProblem.Of(PipelineProblemCodes.DownstreamNoReason, "no reason was given"),
                ct, outcome.Inner).ConfigureAwait(false);
            return;
        }
        logger.LogInformation("Downstream pipeline {TargetId} triggered after run {RunId} succeeded", target.Id, run.Id);
    }

    /// <summary>Recette R-48: a follow-up that could not be started is told where the user looks: on the
    /// upstream run (a warning its page shows) and to the project's subscribers. Never throws: the
    /// upstream run already succeeded and must stay so.</summary>
    private async Task ReportRefusalAsync(
        PipelineRun run, int? projectId, string? targetName, int? targetPipelineId, PipelineProblem reason,
        CancellationToken ct, IReadOnlyList<ProblemDto>? innerProblems = null)
    {
        logger.LogWarning(
            "Downstream chaining after run {RunId}: {Target} not started: {Reason}",
            run.Id, targetName ?? "(on_success)", reason.English);
        // The warning names the follow-up and writes the reason where its sentence says {detail}: the reason's
        // own refusal when it has one (a definition that cannot be prepared), else the reason itself.
        var inner = innerProblems is { Count: > 0 } ? innerProblems : [reason.ToDto()];
        var message = (targetName is null
            ? PipelineProblem.Of(
                PipelineProblemCodes.OnSuccessFollowUpsNotStarted,
                $"The on_success follow-ups were not started: {reason.English}",
                ("detail", reason.English))
            : PipelineProblem.Of(
                PipelineProblemCodes.OnSuccessFollowUpNotStarted,
                $"The on_success follow-up '{targetName}' was not started: {reason.English}",
                ("target", targetName), ("detail", reason.English))).ToStored(inner);
        try
        {
            await repo.AppendRunWarningsAsync(run.Id, [message], ct).ConfigureAwait(false);
            if (projectId is not null)
                await notifications.PublishChainRefusedAsync(
                    run, projectId, targetName ?? "on_success", targetPipelineId, reason.English, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mandatory catch: reporting is best effort and must not fail the upstream run or skip the
            // remaining on_success targets.
            logger.LogWarning(ex, "The refusal of an on_success follow-up of run {RunId} could not be recorded", run.Id);
        }
    }

    private async Task<Dictionary<string, string>> BuildDownstreamVariablesAsync(
        PipelineDownstreamTrigger trigger, PipelineRun run, IReadOnlyCollection<int> chain, CancellationToken ct)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["UPSTREAM_RUN_ID"] = run.Id.ToString(),
            ["UPSTREAM_PIPELINE"] = run.Pipeline!.Name,
            [PipelineScannerTaskFactory.UpstreamTemplateVariable] = run.Pipeline.TemplateReferenceName ?? string.Empty,
            ["UPSTREAM_CHAIN"] = string.Join(",", chain),
            // Additional trigger metadata overrides the static definition value to identify the
            // actual initiating run.
            ["BUILD_TRIGGEREDBY"] = $"on_success:{run.Pipeline!.Name}#{run.Id}"
        };
        var release = trigger.Release?.Trim();
        if (string.Equals(release, LatestReleaseSelector, StringComparison.OrdinalIgnoreCase))
        {
            // The run that just succeeded may itself have published the release ("latest" = "what this
            // run published"), e.g. the candidate's own `type: release` step. A run publishes at most
            // one release per pipeline definition, so the run id resolves it unambiguously.
            var publishedVersion = await repo.FindPublishedReleaseVersionByRunIdAsync(run.Id, ct).ConfigureAwait(false);
            if (publishedVersion is not null) vars["UPSTREAM_RELEASE"] = publishedVersion;
            else logger.LogWarning("Downstream trigger: run {RunId} declared release: latest but published no release of its own", run.Id);
        }
        else if (!string.IsNullOrWhiteSpace(release))
        {
            vars["UPSTREAM_RELEASE"] = release;
        }
        // A release-selecting trigger deploys an immutable published artifact, decoupled on purpose
        // from whatever branch/commit built it (that is the entire point of publishing a release
        // first). Forwarding the upstream branch here would override the target's own configured
        // `source_branch` - e.g. it would make aetheus-deploy-prod resolve to `develop` (the
        // candidate's branch) instead of its declared `main`, and its own prod-deploy-prepare.sh
        // refuses outright when BUILD_SOURCEBRANCH isn't main. Only a non-release chain (a target that
        // wants to build/validate the exact same commit the upstream just built) inherits them.
        if (string.IsNullOrWhiteSpace(release))
        {
            if (run.CommitHash is { Length: 40 or 64 } && run.CommitHash.All(Uri.IsHexDigit)) vars["AETHEUS_SOURCE_COMMIT"] = run.CommitHash;
            if (!string.IsNullOrWhiteSpace(run.BranchName)) vars["AETHEUS_RUN_BRANCH"] = run.BranchName;
        }
        return vars;
    }

    // Hard cap on chained-pipeline depth - a backstop even when no node repeats (e.g. A→B→C→…→Z).
    private const int MaxChainDepth = 10;

}
