// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>Centralizes run status predicates so terminal status handling stays consistent across
/// callers. A guard test requires every terminal value to be listed here.</summary>
public static class PipelineStatusFacts
{
    /// <summary>True when the run is over, whatever the outcome. Nothing more will be dispatched.</summary>
    public static bool IsTerminal(this PipelineStatus status) => status
        is PipelineStatus.Success
        or PipelineStatus.Failed
        or PipelineStatus.Cancelled
        or PipelineStatus.Partial
        or PipelineStatus.RolledBack
        or PipelineStatus.Rejected;

    /// <summary>The same set as <see cref="IsTerminal"/>, as an array a database query can use
    /// (<c>Terminal.Contains(run.Status)</c> translates, a call to <c>IsTerminal()</c> does not).
    /// Derived from it, so the two can never list different statuses.</summary>
    public static readonly PipelineStatus[] Terminal =
        [.. Enum.GetValues<PipelineStatus>().Where(status => status.IsTerminal())];

    /// <summary>
    /// True when the run ended without producing a result anyone should build on. Partial belongs here:
    /// something failed, it was merely allowed not to stop the run.
    /// </summary>
    public static bool IsUnsuccessful(this PipelineStatus status) => status.IsTerminal()
        && status != PipelineStatus.Success;
}
