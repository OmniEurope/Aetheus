// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Shared;

/// <summary>How a shared token renewal ended (<see cref="TokenRenewalGate"/>).</summary>
internal enum TokenRenewalOutcome
{
    NotNeeded,
    Succeeded,
    Rejected,
    Unavailable
}
