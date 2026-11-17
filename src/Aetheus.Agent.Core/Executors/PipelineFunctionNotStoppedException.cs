// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Executors;

/// <summary>
/// A cancelled in-process pipeline function that had not ended when the stop grace elapsed: it may still be
/// mutating (a publication, a file move), so the agent must not report the task as stopped (see
/// <see cref="UnconfirmedCancellationException"/>).
/// </summary>
public sealed class PipelineFunctionNotStoppedException(string functionName, CancellationToken cancellationToken)
    : UnconfirmedCancellationException(
        $"Function '{functionName}' did not end after its cancellation; its mutations may still be running.",
        cancellationToken);
