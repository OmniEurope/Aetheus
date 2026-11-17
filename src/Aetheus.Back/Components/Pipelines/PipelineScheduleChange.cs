// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>A schedule change ready to be written: the expression (null when it was left as written) and
/// the YAML that carries it, or the reason it was refused (never both).</summary>
public sealed record PipelineScheduleChangeResult(string? Cron, string? Yaml, string? Error)
{
    public bool Succeeded => Error is null;
    public static PipelineScheduleChangeResult Ok(string? cron, string yaml) => new(cron, yaml, null);
    public static PipelineScheduleChangeResult Fail(string error) => new(null, null, error);
}

/// <summary>
/// The checks that come before any write of a schedule, in the order a write would need them: the
/// expression built from the proposal, <see cref="ScheduleOccurrenceExpander.IsFiredByTheEngine"/> (Cronos, as
/// the scheduler engine parses it), the text edit, then <c>YamlParsingHelper.ParseAndValidate</c> on the
/// resulting YAML, which must read back the same schedule and trigger. It writes nothing: the caller
/// gets the YAML to write, or the reason not to.
/// </summary>
public static class PipelineScheduleChange
{
    /// <summary>The change that sets <paramref name="proposal"/> on <paramref name="yaml"/>: enabled puts
    /// <c>trigger: schedule</c>, disabled keeps the value and puts <c>trigger: manual</c>.</summary>
    public static PipelineScheduleChangeResult Prepare(string yaml, ScheduleFrequencySpec proposal, bool enabled)
    {
        var cron = ScheduleFrequencyCodec.Encode(proposal);
        return cron is null
            ? PipelineScheduleChangeResult.Fail("This frequency and time cannot be written as a schedule.")
            : PrepareExpression(yaml, cron, enabled);
    }

    /// <summary>The same checks for an expression already written.</summary>
    public static PipelineScheduleChangeResult PrepareExpression(string yaml, string cron, bool enabled)
    {
        if (!ScheduleOccurrenceExpander.IsFiredByTheEngine(cron))
        {
            return PipelineScheduleChangeResult.Fail(
                $"'{cron}' is not a cron expression the engine can fire (six fields, seconds first).");
        }

        return EditAndReadBack(yaml, cron, enabled);
    }

    /// <summary>The change that enables or disables what <paramref name="yaml"/> holds, leaving the value
    /// as it is written. Enabling takes a value the engine can fire; disabling takes any value, also one
    /// the engine cannot read, or none.</summary>
    public static PipelineScheduleChangeResult PrepareEnabled(string yaml, bool enabled)
    {
        if (!enabled)
        {
            return EditAndReadBack(yaml, null, enabled: false);
        }

        var schedule = PipelineScheduleReader.Read(yaml)?.Schedule;
        return schedule is null
            ? PipelineScheduleChangeResult.Fail("This pipeline has no schedule to enable.")
            : PrepareExpression(yaml, schedule, enabled: true);
    }

    // A webhook pipeline is started from outside: a schedule would silently replace that, and the trigger
    // has one value. Changing what starts it stays a decision made in the definition.
    private static PipelineScheduleChangeResult EditAndReadBack(string yaml, string? cron, bool enabled)
    {
        if (string.Equals(PipelineScheduleReader.Read(yaml)?.Trigger, "webhook", StringComparison.OrdinalIgnoreCase))
        {
            return PipelineScheduleChangeResult.Fail("This pipeline is started by a webhook, so it cannot be scheduled here.");
        }

        var trigger = enabled ? PipelineScheduleYamlEditor.ScheduleTrigger : PipelineScheduleYamlEditor.ManualTrigger;
        var edit = PipelineScheduleYamlEditor.Apply(yaml, cron, trigger);
        if (!edit.Succeeded)
        {
            return PipelineScheduleChangeResult.Fail(edit.Error!);
        }

        var definition = YamlParsingHelper.ParseAndValidate(edit.Yaml!, out var error);
        if (definition is null)
        {
            return PipelineScheduleChangeResult.Fail($"The pipeline YAML would be refused: {error}");
        }

        var scheduleReadBack = cron is null || definition.Schedule?.Trim() == cron;
        return scheduleReadBack && string.Equals(definition.Trigger, trigger, StringComparison.OrdinalIgnoreCase)
            ? PipelineScheduleChangeResult.Ok(cron, edit.Yaml!)
            : PipelineScheduleChangeResult.Fail("The pipeline would not read back the schedule that was set.");
    }
}
