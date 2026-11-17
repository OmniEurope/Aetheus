// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>What a pipeline YAML says about its schedule: the <c>trigger:</c> value and the raw
/// <c>schedule:</c> cron expression (null when absent or blank).</summary>
public sealed record PipelineScheduleInfo(string Trigger, string? Schedule)
{
    public bool IsScheduled => string.Equals(Trigger, "schedule", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads <c>trigger</c> and <c>schedule</c> from a pipeline's own YAML through
/// <see cref="YamlParsingHelper"/>. Like the scheduler engine, it reads the pipeline's own document
/// only: a <c>schedule:</c> that exists solely in an <c>extends:</c> parent is not seen here.
/// </summary>
public static class PipelineScheduleReader
{
    /// <summary>The schedule data of <paramref name="yaml"/>, or null when the YAML is empty or does
    /// not parse. Never throws.</summary>
    public static PipelineScheduleInfo? Read(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return null;
        }

        var definition = YamlParsingHelper.ParseAndValidate(yaml);
        if (definition is null)
        {
            return null;
        }

        var schedule = string.IsNullOrWhiteSpace(definition.Schedule) ? null : definition.Schedule.Trim();
        return new PipelineScheduleInfo(definition.Trigger, schedule);
    }
}
