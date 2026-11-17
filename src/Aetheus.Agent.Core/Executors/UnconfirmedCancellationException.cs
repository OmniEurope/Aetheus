// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Executors;

/// <summary>
/// A cancelled task whose stop could not be proven (R-12). It is still an
/// <see cref="OperationCanceledException"/>, so every existing cancellation handler keeps working, but the
/// agent must not tell the control plane that the task stopped: the control plane then waits its maximum
/// delay and proceeds, logging that no acknowledgement came.
/// </summary>
public abstract class UnconfirmedCancellationException(string message, CancellationToken cancellationToken)
    : OperationCanceledException(message, cancellationToken);
