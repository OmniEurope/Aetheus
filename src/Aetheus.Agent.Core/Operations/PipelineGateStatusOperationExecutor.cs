// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Aggregate published gate statuses, treating absent evidence as failure. Reject values
/// outside 0 and 1 rather than hiding invalid producer output.</summary>
public sealed class PipelineGateStatusOperationExecutor : EnvironmentOperationExecutor
{
    public override bool CanHandle(OperationKind kind) => kind == OperationKind.PipelineGateStatus;

    public override async Task<ExecutorResult> ExecuteAsync(
        OperationKind kind, string target, IReadOnlyDictionary<string, string> envVars,
        int timeoutSeconds, Func<string, TaskLogLevel, Task> onOutput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envVars);
        ArgumentNullException.ThrowIfNull(onOutput);

        if (kind != OperationKind.PipelineGateStatus) return new ExecutorResult(-1, false);

        var names = SplitNames(envVars.GetValueOrDefault(PipelineGateStatusVariables.StatusVariables));
        if (names.Count == 0)
        {
            await onOutput(
                "A gate-status step names no status to aggregate, so it would pass unconditionally.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return new ExecutorResult(1, false, "empty-gate", "No status variables to aggregate.");
        }

        var label = envVars.GetValueOrDefault(PipelineGateStatusVariables.Label) ?? "Gate";
        var verdict = 0;
        foreach (var name in names)
        {
            // GetValueOrDefault would collapse "absent" and "published as empty" into the same thing.
            // They mean the same here (neither is evidence), but saying so explicitly is what keeps
            // the missing case from ever being read as a pass.
            var published = envVars.TryGetValue(name, out var raw) ? raw?.Trim() : null;
            if (string.IsNullOrEmpty(published))
            {
                await onOutput(
                    $"{label}: '{name}' was never published, which counts as a failure, not a pass.",
                    TaskLogLevel.Error).ConfigureAwait(false);
                verdict = 1;
                continue;
            }

            if (published is not ("0" or "1"))
            {
                await onOutput(
                    $"{label}: '{name}' holds '{published}', which is neither 0 nor 1, so the step "
                    + "that produced it did not report a verdict this gate can read.",
                    TaskLogLevel.Error).ConfigureAwait(false);
                return new ExecutorResult(
                    1, false, "invalid-status", $"'{name}' holds an unreadable status.");
            }

            await onOutput($"{label}: {name} = {published}.", TaskLogLevel.Info).ConfigureAwait(false);
            if (published == "1") verdict = 1;
        }

        var publishAs = envVars.GetValueOrDefault(PipelineGateStatusVariables.PublishAs);
        if (!string.IsNullOrWhiteSpace(publishAs))
            await onOutput(
                $"##aetheus[setvariable name={publishAs}]"
                + verdict.ToString(CultureInfo.InvariantCulture),
                TaskLogLevel.Info).ConfigureAwait(false);

        var blocking = string.Equals(
            envVars.GetValueOrDefault(PipelineGateStatusVariables.Blocking),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (verdict == 0)
        {
            await onOutput($"{label}: passing evidence recorded.", TaskLogLevel.Info).ConfigureAwait(false);
            return new ExecutorResult(0, false);
        }

        await onOutput(
            $"{label}: at least one status failed or was unavailable.",
            blocking ? TaskLogLevel.Error : TaskLogLevel.Warning).ConfigureAwait(false);

        // A non-blocking gate still publishes its verdict; it just leaves the decision to whoever
        // reads the variable, which is how an advisory gate differs from a silent one.
        return blocking
            ? new ExecutorResult(1, false, "gate-failed", $"{label} failed.")
            : new ExecutorResult(0, false);
    }

    /// <summary>Names are comma-separated, trimmed, and de-duplicated while keeping their order, so a
    /// list repeating a status does not make it count twice in the log.</summary>
    private static List<string> SplitNames(string? raw)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return names;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (seen.Add(part))
                names.Add(part);
        return names;
    }
}
