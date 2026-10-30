// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Pure formatting / badge / colour / aggregation helpers for the run view. Extracted from the
/// former <c>PipelineRun.*.cs</c> partials into a real collaborator; the <c>PipelineRun.razor</c>
/// markup imports it via <c>@using static</c> so call sites stay unqualified.
/// </summary>
internal static class PipelineRunFormatting
{
    public static bool IsWindows(string? os) =>
        os?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true;

    public static string MatrixGroupKey(string stageName, string stepName) => $"{stageName}::{stepName}";

    public static TaskExecutionStatus GetAggregateStatus(List<PipelineStepRunDto> steps)
    {
        if (steps.Any(s => s.Status == TaskExecutionStatus.Failed)) return TaskExecutionStatus.Failed;
        if (steps.Any(s => s.Status == TaskExecutionStatus.Running)) return TaskExecutionStatus.Running;
        if (steps.All(s => s.Status == TaskExecutionStatus.Success)) return TaskExecutionStatus.Success;
        // A stage the engine cancelled because its condition was false is terminal, and the run has
        // already moved past it. Falling through to Pending left it reading as "still to come" for
        // the rest of the run, on a stage that will never start.
        if (steps.Count > 0 && steps.All(s => s.Status == TaskExecutionStatus.Cancelled))
            return TaskExecutionStatus.Cancelled;
        return TaskExecutionStatus.Pending;
    }

    /// <summary>Recette R1-80: a stage whose steps all completed, at least one of them a control that
    /// ran and found failures (<see cref="PipelineStepRunDto.IsRejected"/>). A failed or running step
    /// keeps the stage failed or running: an error outranks a rejection.</summary>
    public static bool IsRejectedStage(List<PipelineStepRunDto> steps) =>
        GetAggregateStatus(steps) == TaskExecutionStatus.Success && steps.Any(step => step.IsRejected);

    /// <summary>The condition that kept a whole stage from running, or null when the stage was not
    /// skipped by one. Only a stage whose every step was cancelled by an unmet condition qualifies:
    /// a stage cancelled after an upstream failure carries no condition and must not claim one.</summary>
    public static string? GetSkippedCondition(List<PipelineStepRunDto> steps) =>
        steps.Count > 0
        && steps.All(step => step.Status == TaskExecutionStatus.Cancelled
                             && !string.IsNullOrWhiteSpace(step.SkippedCondition))
            ? steps[0].SkippedCondition
            : null;

    public static string FormatDuration(DateTime? started, DateTime? completed)
    {
        if (started is null) return string.Empty;
        // ServerClock, not DateTime: `started` was written by the server, and subtracting it from this
        // browser's clock measures the gap between two machines as much as the elapsed time. A browser
        // running behind reads a start time in its own future, the negative is clamped just below, and a
        // build that is genuinely running shows "0s" for as long as the skew lasts.
        var end = completed ?? (started.Value.Kind == DateTimeKind.Utc
            ? ServerClock.UtcNow
            : ServerClock.UtcNow.ToLocalTime());
        var duration = end - started.Value;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return DisplayFormatting.Duration(duration);
    }

    public static string FormatSize(long bytes) => DisplayFormatting.Size(bytes);

    public static OmniTone GetRunBadge(PipelineStatus status) => PipelineHelper.GetRunBadge(status);

    public static OmniTone GetStepBadge(TaskExecutionStatus status) => status switch
    {
        TaskExecutionStatus.Success => OmniTone.Success,
        TaskExecutionStatus.Failed => OmniTone.Danger,
        TaskExecutionStatus.Running => OmniTone.Accent,
        _ => OmniTone.Neutral
    };

    // RLBK: the deploy agent sets this output variable when a failed deploy's health gate triggered a
    // rollback that successfully restored the previous release - the step "failed" but prod is up again.
    public const string RolledBackVar = "DEPLOY_ROLLED_BACK";

    /// <summary>True when a failed deploy step was followed by a rollback that restored the previous
    /// release (so the app is active, not down) - surfaced as a distinct "Rolled back" state.</summary>
    public static bool IsRolledBack(PipelineStepRunDto step) =>
        step.Status == TaskExecutionStatus.Failed
        && step.OutputVariables.TryGetValue(RolledBackVar, out var v)
        && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    // Failed steps stay red, with an outline for failures that allow execution to continue.
    // Rolled-back deployments use the warning colour to distinguish restored service.
    public static OmniTone GetStepBadge(PipelineStepRunDto step) =>
        IsRolledBack(step) ? OmniTone.Warning
        : IsSkippedWithoutWork(step) ? OmniTone.Neutral
        : step.IsRejected ? OmniTone.Warning
        : GetStepBadge(step.Status);

    /// <summary>A step that finished Success without doing its work, such as an
    /// <c>allow_missing</c> restore that found no artifact. Drawn as skipped, never as a green
    /// tick.</summary>
    public static bool IsSkippedWithoutWork(PipelineStepRunDto step) =>
        step.Status == TaskExecutionStatus.Success && !string.IsNullOrWhiteSpace(step.SkippedReason);

    /// <summary>A failure the pipeline was told to continue past.</summary>
    public static bool IsNonBlockingFailure(PipelineStepRunDto step) =>
        step is { Status: TaskExecutionStatus.Failed, ContinueOnError: true } && !IsRolledBack(step);

    /// <summary>A false condition is persisted as <see cref="TaskExecutionStatus.Cancelled"/>
    /// because the enum has no Skipped value. Status displays must inspect <c>SkippedCondition</c>
    /// to distinguish it from cancellation.</summary>
    public static bool IsSkippedByCondition(PipelineStepRunDto step) =>
        step.Status == TaskExecutionStatus.Cancelled && !string.IsNullOrWhiteSpace(step.SkippedCondition);

    /// <summary>Status label for a step badge - "Rolled back" for a restored-after-failure deploy,
    /// "Skipped" for a step whose condition was false, otherwise the localized execution
    /// status.</summary>
    public static string StepStatusText(PipelineStepRunDto step, IStringLocalizer localizer) =>
        IsRolledBack(step) ? localizer["RolledBack"].Value
        : IsSkippedWithoutWork(step) ? localizer["StepSkippedArtifactMissing"].Value
        : IsSkippedByCondition(step) ? localizer["Skipped"].Value
        : step.IsRejected ? localizer.Localize(PipelineStatus.Rejected)
        : localizer.Localize(step.Status);

    /// <summary>Tooltip of a step badge: why a failure did not block, why a step did no work, or the
    /// condition that kept it from running.</summary>
    public static string? StepStatusTitle(PipelineStepRunDto step, IStringLocalizer localizer) =>
        IsNonBlockingFailure(step) && step.IsAdvisory ? localizer["StepAdvisoryHint"].Value
        : IsNonBlockingFailure(step) ? localizer["StepFailedNonBlocking"].Value
        : IsSkippedWithoutWork(step) ? step.SkippedReason
        : IsSkippedByCondition(step) ? step.SkippedCondition
        : step.IsRejected ? localizer["StepRejectedHint"].Value
        : null;

    /// <summary>The icon a skipped status carries, so "skipped" is not told by its text alone and
    /// never by a colour it shares with Cancelled. Null for every other status (text-only
    /// badge).</summary>
    public static OmniIconName? StepStatusIcon(PipelineStepRunDto step) =>
        IsSkippedByCondition(step) || IsSkippedWithoutWork(step) ? SkippedIcon : null;

    /// <summary>The icon of a step's status badge: a text badge always carries one (badge rule of
    /// 2026-10-25, recette R-59), the same icons as the run statuses, skipped keeping its own.</summary>
    public static OmniIconName StepBadgeIcon(PipelineStepRunDto step) =>
        StepStatusIcon(step)
        ?? (IsRolledBack(step) ? OmniIconName.Reset
            : step.IsRejected ? OmniIconName.ShieldSlash
            : step.Status switch
            {
                TaskExecutionStatus.Success => OmniIconName.Check,
                TaskExecutionStatus.Failed or TaskExecutionStatus.Timeout => OmniIconName.Close,
                TaskExecutionStatus.Running => OmniIconName.Sync,
                TaskExecutionStatus.Cancelled => OmniIconName.Prohibit,
                _ => OmniIconName.Timer
            });

    /// <summary>One icon for "skipped" everywhere (step badge, Gantt stage and step
    /// rows).</summary>
    public const OmniIconName SkippedIcon = OmniIconName.ArrowUUpRight;

    // UCHN: cascade lineage read from a run's variables - the downstream-trigger handler forwards
    // UPSTREAM_PIPELINE (immediate parent name), UPSTREAM_RUN_ID (parent run, linkable) and
    // UPSTREAM_CHAIN (comma-separated ancestor pipeline ids). All null/0 for a directly-launched run.
    public static string? UpstreamPipeline(IReadOnlyDictionary<string, string> vars) =>
        vars.TryGetValue("UPSTREAM_PIPELINE", out var p) && p.Length > 0 ? p : null;

    public static int? UpstreamRunId(IReadOnlyDictionary<string, string> vars) =>
        vars.TryGetValue("UPSTREAM_RUN_ID", out var s) && int.TryParse(s, out var id) ? id : null;

    public static int UpstreamChainDepth(IReadOnlyDictionary<string, string> vars) =>
        vars.TryGetValue("UPSTREAM_CHAIN", out var c) && !string.IsNullOrWhiteSpace(c)
            ? c.Split(',', StringSplitOptions.RemoveEmptyEntries).Length
            : 0;

    public static string ShortSha(string commitHash) => commitHash[..Math.Min(8, commitHash.Length)];

    // Every delivery pipeline stamps the built application version into APP_VERSION (see .pipeline/*.yaml),
    // so the run view can name the version a release actually shipped instead of only its release name.
    public const string AppVersionVar = "APP_VERSION";

    /// <summary>The application version this run built, read from the resolved <c>APP_VERSION</c>
    /// variable and falling back to the last step that emitted it as an output. Null when the run's
    /// pipeline does not stamp a version.</summary>
    public static string? BuiltAppVersion(PipelineRunDto run)
    {
        if (run.ResolvedVariables.TryGetValue(AppVersionVar, out var resolved) && !string.IsNullOrWhiteSpace(resolved))
            return resolved.Trim();
        var emitted = run.Steps
            .SelectMany(step => step.OutputVariables)
            .Where(output => string.Equals(output.Key, AppVersionVar, StringComparison.Ordinal)
                             && !string.IsNullOrWhiteSpace(output.Value))
            .Select(output => output.Value.Trim())
            .LastOrDefault();
        return emitted;
    }

    public static string FormatPercent(double rate) => $"{rate * 100:F1}%";

    public static string CoverageColor(double rate) => rate switch
    {
        >= 0.8 => "var(--omni-u-text-success)",
        >= 0.5 => "var(--omni-u-text-warning)",
        _ => "var(--omni-u-text-danger)"
    };

    public static string CoverageColorClass(double rate) => rate switch
    {
        >= 0.8 => "omni-u-text-success",
        >= 0.5 => "omni-u-text-warning",
        _ => "omni-u-text-danger"
    };

    /// <summary>Coverage gauge colours use the same tiers as <see cref="CoverageColorClass"/>: OE
    /// palette indices 1, 2 and 4 denote green, orange and red.</summary>
    public static int CoverageChartColorIndex(double rate) => rate switch
    {
        >= 0.8 => 1,
        >= 0.5 => 2,
        _ => 4
    };

    /// <summary>The resource key naming the tier <see cref="CoverageColorClass"/> colours, on the
    /// same thresholds, so the verdict is also given in words and not by the hue alone.</summary>
    public static string CoverageTierKey(double rate) => rate switch
    {
        >= 0.8 => "CoverageTierGood",
        >= 0.5 => "CoverageTierFair",
        _ => "CoverageTierLow"
    };

    // Average cyclomatic complexity: <=5 healthy, <=10 watch, higher is hot.
    public static string ComplexityColorClass(double avg) => avg switch
    {
        <= 5 => "omni-u-text-success",
        <= 10 => "omni-u-text-warning",
        _ => "omni-u-text-danger"
    };

    // CRAP: <=5 healthy, <=30 watch, higher is risky (hard-to-test complex code).
    public static string CrapColorClass(double crap) => crap switch
    {
        <= 5 => "omni-u-text-success",
        <= 30 => "omni-u-text-warning",
        _ => "omni-u-text-danger"
    };

    // Cobertura filenames are often long source paths; show the last two segments, full path on hover.
    public static string ShortPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? path : string.Join('/', parts[^2..]);
    }

    public static bool IsWebUrl(string? url) =>
        !string.IsNullOrEmpty(url)
        && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    public static string? BuildCommitUrl(string? repoUrl, string? sha)
    {
        if (!IsWebUrl(repoUrl) || string.IsNullOrEmpty(sha)) return null;
        var baseUrl = repoUrl!.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repoUrl[..^4] : repoUrl;
        return $"{baseUrl.TrimEnd('/')}/commit/{sha}";
    }

    public static bool IsBlockingWarning(string warning) =>
        warning.Contains("no online server", StringComparison.OrdinalIgnoreCase)
        || warning.Contains("no online pipeline runner", StringComparison.OrdinalIgnoreCase)
        || warning.Contains("container isolation", StringComparison.OrdinalIgnoreCase)
        || warning.Contains("containers-only", StringComparison.OrdinalIgnoreCase);

    public static bool IsLintWarning(string message) =>
        message.Contains("warning", StringComparison.OrdinalIgnoreCase);

    // S-DES-22: true when two or more steps of a stage overlap in time (ran in parallel).
    public static bool StageRunsInParallel(StageViewModel stage)
    {
        var timed = stage.Steps.Where(s => s.StartedAt.HasValue).ToList();
        for (var i = 0; i < timed.Count; i++)
        {
            for (var j = i + 1; j < timed.Count; j++)
            {
                var aStart = timed[i].StartedAt!.Value;
                var aEnd = timed[i].CompletedAt ?? DateTime.Now;
                var bStart = timed[j].StartedAt!.Value;
                var bEnd = timed[j].CompletedAt ?? DateTime.Now;
                if (aStart < bEnd && bStart < aEnd) return true;
            }
        }
        return false;
    }

    private static readonly PipelineStatus[] TerminalRunStatuses =
        [.. Enum.GetValues<PipelineStatus>().Where(PipelineStatusFacts.IsTerminal)];

    public static bool IsTerminal(PipelineStatus status) => TerminalRunStatuses.Contains(status);

    /// <summary>Orphaned / abnormally-finalized runs can reach a terminal status with a null
    /// <c>CompletedAt</c>. Derive the end from the last finished step so the run renders as the
    /// completed (cancelled/failed) run it actually is.</summary>
    public static PipelineRunDto NormalizeRunCompletion(PipelineRunDto run)
    {
        if (run.CompletedAt is not null || !TerminalRunStatuses.Contains(run.Status))
            return run;
        var lastStepEnd = run.Steps.Where(s => s.CompletedAt.HasValue).Select(s => s.CompletedAt).Max();
        return lastStepEnd is null ? run : run with { CompletedAt = lastStepEnd };
    }

    private static readonly Regex BlockingTargetRegex = new(
        @"no online server (?:matches agent|found in pool|found in environment) '(?<name>[^']+)'",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record BlockingWarningView(string Message, string? ConfigHref, string? ConfigName);

    public static BlockingWarningView ToBlockingView(string warning)
    {
        var match = BlockingTargetRegex.Match(warning);
        if (!match.Success) return new(warning, null, null);

        var href = warning.Contains("in environment", StringComparison.OrdinalIgnoreCase)
            ? "/environments"
            : "/servers";
        return new(warning, href, match.Groups["name"].Value);
    }
}
