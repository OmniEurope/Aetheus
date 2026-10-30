// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>What one step of the project copy wizard reads and changes of the wizard that renders it. The
/// step depends on this contract only, so the copy flow (which creates the step) and the step do not depend on
/// each other.</summary>
public interface IPipelineProjectCopyFlow
{
    IReadOnlyList<ProjectDto> Projects { get; }
    int? TargetProjectId { get; }
    string Prefix { get; }
    int? SourceProjectId { get; }
    string SourcePrefix { get; }
    PipelineProjectCopyPlanDto? Plan { get; }
    bool PlanLoading { get; }
    bool PlanFailed { get; }
    IReadOnlySet<int> Selected { get; }
    PipelineProjectCopyResultDto? Result { get; }
    string? CopyError { get; }
    bool ReadinessLoading { get; }

    /// <summary>True when neither the new project nor its repository names a branch: the copies are written without a
    /// <c>source_branch:</c> (R-76), which the first step says.</summary>
    bool SourceBranchMissing { get; }
    ProjectDto? TargetProject { get; }
    ProjectDto? SourceProject { get; }
    IEnumerable<ProjectDto> SourceProjects { get; }
    IReadOnlyList<PipelineCopyItemDto> SelectedPipelines { get; }

    void SetTargetProject(int? projectId);
    void SetPrefix(string value);
    void SetSourceProject(int? projectId);
    void SetSourcePrefix(string value);
    void Toggle(int sourcePipelineId, bool selected);
}
