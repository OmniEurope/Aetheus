// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Executors;

/// <summary>
/// A cancelled command whose process tree could not be proven terminated (see
/// <see cref="UnconfirmedCancellationException"/>).
/// </summary>
public sealed class ProcessTreeNotTerminatedException(CancellationToken cancellationToken)
    : UnconfirmedCancellationException("The cancelled command's process tree could not be proven terminated.", cancellationToken);
