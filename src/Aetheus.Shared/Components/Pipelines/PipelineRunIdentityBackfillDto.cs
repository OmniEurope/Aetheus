// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>ADR-060: the runs of the instance, and those still without a public identifier.</summary>
public sealed record PipelineRunIdentityBackfillDto
{
    public int Total { get; init; }
    public int Remaining { get; init; }
}
