// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

public sealed record PipelinePaginationRequest : PaginationRequest
{
    public PipelineTriggerType? TriggerType { get; init; }
    public int? EnvironmentId { get; init; }
    public int? ProjectServerId { get; init; }
    public int? ProjectId { get; init; }
    public int? ServerId { get; init; }
}
