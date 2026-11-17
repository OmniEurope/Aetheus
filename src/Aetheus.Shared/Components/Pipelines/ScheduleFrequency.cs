// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>The named frequencies the build planner offers. The localized text belongs to the UI;
/// <see cref="Custom"/> stands for any cron expression the planner does not produce itself.</summary>
public enum ScheduleFrequency
{
    EveryEvening,
    Weekdays,
    EveryTwoDays,
    Weekly,
    Monthly,
    Custom
}
