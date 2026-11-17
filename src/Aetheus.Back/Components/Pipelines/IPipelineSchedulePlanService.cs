// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The pipeline a schedule is previewed for. <see cref="OrganizationId"/> is null for a pipeline
/// that belongs to no organization.</summary>
public sealed record PipelineScheduleTarget(int PipelineId, string Name, string YamlDefinition, int? OrganizationId);

public interface IPipelineSchedulePlanService
{
    /// <summary>The organization's scheduled pipelines over the UTC window <c>[from, to)</c>, limited to
    /// <paramref name="accessiblePipelineIds"/> (null: no restriction). The caller has already checked
    /// that the organization is one it may read. The plan also holds the scheduled actions of the organization's
    /// servers (recette R-8) the caller's <paramref name="serverActions"/> scope reaches, drawn with the pipelines
    /// (their overlaps count together). An invalid window is a <see cref="BadRequestException"/>.
    /// <paramref name="sourceBranches"/> (recette R-5, null: none) names, per pipeline id, the branch of the project's
    /// repository the schedule is read on instead of the pipeline's own: it changes what the plan shows and writes nothing.</summary>
    Task<SchedulePlanDto> GetPlanAsync(
        int organizationId, DateTime from, DateTime to, IReadOnlyCollection<int>? accessiblePipelineIds,
        ServerActionScope serverActions, IReadOnlyDictionary<int, string>? sourceBranches, CancellationToken ct);

    /// <summary>The pipeline to preview a schedule for, or null when it does not exist.</summary>
    Task<PipelineScheduleTarget?> FindTargetAsync(int pipelineId, CancellationToken ct);

    /// <summary>What <paramref name="proposal"/> would give for <paramref name="target"/> over the UTC window
    /// <c>[from, to)</c>: its occurrences, and the overlaps with the other enabled pipelines of the
    /// organization the caller may read (<paramref name="accessiblePipelineIds"/>, null: no restriction).
    /// Nothing is written. An invalid window, or a proposal that cannot be written as an expression the
    /// engine fires, is a <see cref="BadRequestException"/>; a YAML that would not take the change is
    /// reported in <see cref="SchedulePreviewDto.YamlError"/>. The caller has already checked that it may
    /// read <paramref name="target"/>.</summary>
    Task<SchedulePreviewDto> PreviewAsync(
        PipelineScheduleTarget target, ScheduleFrequencySpec proposal, DateTime from, DateTime to,
        IReadOnlyCollection<int>? accessiblePipelineIds, CancellationToken ct);
}
