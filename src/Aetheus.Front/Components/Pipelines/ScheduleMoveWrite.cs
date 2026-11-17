// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>A drop on the calendar that changes a schedule, as the page writes it: the task, the request against
/// what the page read, the sentence the success toast says and the request "Undo" writes (null when the previous
/// value cannot be written).</summary>
public sealed record ScheduleMoveWrite(
    ScheduledTaskDto Task,
    UpdatePipelineScheduleRequest Request,
    string Detail,
    UpdatePipelineScheduleRequest? Undo);
