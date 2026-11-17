// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// What the schedule dialog hands back to the planner page when it closes. <see cref="Saved"/>: the schedule
/// was written (the dialog already said so in a toast), <see cref="Summary"/> says it in words and
/// <see cref="Undo"/> is the request that puts the previous value back (null when the previous value cannot be
/// written, a custom expression). <see cref="Reload"/>:
/// the write was refused for a reason a reload mends (403, 404, 409), the page reads the plan again.
/// </summary>
public sealed record PipelineScheduleDialogResult(
    bool Saved,
    bool Reload,
    int PipelineId = 0,
    string PipelineName = "",
    string Summary = "",
    string? OwnerUsername = null,
    bool OwnerInactive = false,
    UpdatePipelineScheduleRequest? Undo = null);
