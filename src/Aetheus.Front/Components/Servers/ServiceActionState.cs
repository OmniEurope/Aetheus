// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers;

/// <summary>Where an action launched on a service stands, from its task.</summary>
public enum ServiceActionPhase
{
    Queued,
    Running,
    Succeeded,
    Failed
}

/// <summary> The action in flight, or just finished, on one service row. </summary> <param
/// name="TaskId">The agent task that carries the action; the row links to it.</param> <param
/// name="ActionKey">Localization key of the action's verb (Start, Stop, Restart, Install,
/// Uninstall).</param> <param name="ExitCode">The task's exit code once it has failed, when the
/// agent reported one.</param>
public sealed record ServiceActionState(int TaskId, string ActionKey, ServiceActionPhase Phase, int? ExitCode = null)
{
    /// <summary>True while the agent has not finished: the row's buttons stay busy.</summary>
    public bool InFlight => Phase is ServiceActionPhase.Queued or ServiceActionPhase.Running;
}
