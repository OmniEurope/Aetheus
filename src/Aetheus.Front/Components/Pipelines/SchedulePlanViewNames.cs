// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>The planner's four views as they are named in the URL and in the view picker. Kept apart from
/// <see cref="PipelineSchedulePlanView"/> so the URL and window helpers it uses do not depend on it back.</summary>
internal static class SchedulePlanViewNames
{
    internal const string Night = "night";
    internal const string Day = "day";
    internal const string Week = "week";
    internal const string Cards = "cards";
}
