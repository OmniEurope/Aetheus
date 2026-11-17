// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Servers;

/// <summary>ADR-060: the tasks of the instance, and those still without a public identifier or number.</summary>
public sealed record TaskIdentityBackfillDto
{
    public int Total { get; init; }
    public int Remaining { get; init; }
}
