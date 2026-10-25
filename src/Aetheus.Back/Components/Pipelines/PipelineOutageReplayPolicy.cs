// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Which pipeline operations may be replayed on their own after an outage, and which must wait for a
/// human.
///
/// This lived in <c>Services/TaskTimeoutService</c>, a generic timeout sweeper, until the 2026-08-20
/// audit (A360-35) pointed out that only the Pipelines module knows what a <c>bluegreen-*</c> step
/// implies. A timeout service deciding which deployments are safe to re-run is the module's knowledge
/// leaking into a neighbour, and the neighbour is where it would rot: a new deployment operation added
/// here would have had to be remembered over there.
/// </summary>
public static class PipelineOutageReplayPolicy
{
    /// <summary>
    /// Operations that must never be replayed on their own after an outage. A deployment interrupted
    /// half-way has already changed the target: re-running it blind can switch traffic to a stack whose
    /// migration never finished, or commit a cutover nobody validated. These fail closed and wait.
    /// </summary>
    private static readonly HashSet<OperationKind> NeverReplayed =
    [
        OperationKind.PipelineDeploy,
        OperationKind.PipelineCreateRelease,
        OperationKind.BlueGreenMigrate,
        OperationKind.BlueGreenUp,
        OperationKind.BlueGreenSwitch,
        OperationKind.BlueGreenCommit,
        OperationKind.BlueGreenRollback
    ];

    /// <summary>
    /// BACK-025 (user decision): operations whose handler is proven to have no effect when run twice, so
    /// a copy already started may be run again. None today. A started shell command may already have
    /// written to a third party; replaying it blind risks a second real effect, so a started task fails
    /// instead, and an operation joins this set only with that proof.
    /// </summary>
    private static readonly HashSet<OperationKind> IdempotentOnceStarted = [];

    /// <summary>The reason a started task that was not replayed fails with.</summary>
    public const string NotReplayedReason =
        "Agent session replaced while the task was running; not replayed automatically.";

    /// <summary>The reason a never-started task that was not replayed fails with (a deployment operation,
    /// a task past the grace, a task no run is waiting for).</summary>
    public const string NotReplayedBeforeStartReason =
        "Agent session replaced before the task started; not replayed automatically.";

    /// <summary><c>true</c> when the operation may be requeued for a replacement agent session.</summary>
    public static bool MayReplayAfterAnOutage(OperationKind operation) => !NeverReplayed.Contains(operation);

    /// <summary><c>true</c> when a task may be requeued after an outage: never started, or started but
    /// explicitly idempotent, and in both cases not a deployment operation.</summary>
    public static bool MayReplayAfterAnOutage(OperationKind operation, bool started) =>
        MayReplayAfterAnOutage(operation) && (!started || IdempotentOnceStarted.Contains(operation));
}
