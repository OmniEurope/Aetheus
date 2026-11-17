// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Services;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>The outcome of a text edit: the new YAML, or the reason it was refused (never both).</summary>
public sealed record ScheduleYamlEditResult(string? Yaml, string? Error)
{
    public bool Succeeded => Error is null;
    public static ScheduleYamlEditResult Ok(string yaml) => new(yaml, null);
    public static ScheduleYamlEditResult Fail(string error) => new(null, error);
}

/// <summary>
/// Changes the top-level <c>schedule:</c> value and the <c>trigger:</c> of a pipeline YAML by editing its
/// text, so indentation, quote style, comments and line endings stay as written (a re-serialization would
/// drop the comments). Only the top-level keys are read: a <c>schedule:</c> in a stage, a step, a template
/// block or a script is not the key. It never trusts its own edit: it parses the original and the result and
/// refuses unless the two documents are equal except for the keys asked to change. A YAML that does not
/// parse, a repeated key, or a layout of the value it cannot keep (a block, a flow, an anchor, a value
/// running over several lines) is refused rather than guessed.
/// </summary>
public static class PipelineScheduleYamlEditor
{
    public const string ManualTrigger = "manual";
    public const string ScheduleTrigger = "schedule";

    private const string ScheduleKey = "schedule";
    private const string TriggerKey = "trigger";

    // The plain deserializer: scalars stay strings, maps and lists stay generic, whatever the pipeline schema.
    private static readonly IDeserializer GenericDeserializer = new DeserializerBuilder().Build();

    /// <summary>Sets <c>schedule:</c> to <paramref name="cron"/> and/or <c>trigger:</c> to
    /// <paramref name="trigger"/> (<see cref="ManualTrigger"/> or <see cref="ScheduleTrigger"/>); null leaves
    /// that key as it is. A missing key is added (the trigger after <c>name:</c>, the schedule after the
    /// trigger); <c>manual</c> on a pipeline that has no trigger key changes nothing, it is the default.
    /// <see cref="ScheduleYamlEditResult.Yaml"/> is the document to write, never half edited.</summary>
    public static ScheduleYamlEditResult Apply(string? yaml, string? cron, string? trigger)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return ScheduleYamlEditResult.Fail("The pipeline YAML is empty.");
        }

        if (cron is null && trigger is null)
        {
            return ScheduleYamlEditResult.Fail("Nothing to change: give a schedule, a trigger or both.");
        }

        var requestError = ValidateRequest(cron, trigger);
        var original = requestError is null ? Parse(yaml, out requestError) : null;
        if (original is null)
        {
            return ScheduleYamlEditResult.Fail(requestError ?? "The pipeline YAML does not parse.");
        }

        var editError = Edit(yaml, cron, trigger, out var edited);
        if (edited is null)
        {
            return ScheduleYamlEditResult.Fail(editError ?? "The pipeline YAML could not be edited.");
        }

        var expected = new Dictionary<object, object?>(original);
        if (cron is not null)
        {
            expected[ScheduleKey] = cron;
        }

        if (trigger is not null && (trigger != ManualTrigger || expected.ContainsKey(TriggerKey)))
        {
            expected[TriggerKey] = trigger;
        }

        var verification = Verify(edited, expected);
        return verification is null ? ScheduleYamlEditResult.Ok(edited) : ScheduleYamlEditResult.Fail(verification);
    }

    private static string? ValidateRequest(string? cron, string? trigger)
    {
        if (trigger is not (null or ManualTrigger or ScheduleTrigger))
        {
            return $"The trigger must be '{ManualTrigger}' or '{ScheduleTrigger}'.";
        }

        if (cron is not null && !IsWritable(cron))
        {
            return "The schedule is empty or holds characters that cannot be written safely in the YAML.";
        }

        return null;
    }

    /// <summary>Quotes, backslashes, control characters and edge spaces would need escaping: refused, a
    /// cron expression never has them.</summary>
    private static bool IsWritable(string value) =>
        value.Length > 0
        && value.Trim() == value
        && !value.Any(c => char.IsControl(c) || c is '"' or '\'' or '\\');

    private static string? Edit(string yaml, string? cron, string? trigger, out string? edited)
    {
        edited = null;
        var text = RootMappingText.Create(yaml, out var layoutError);
        if (text is null)
        {
            return layoutError;
        }

        foreach (var key in new[] { ScheduleKey, TriggerKey })
        {
            if (text.CountKey(key) > 1)
            {
                return $"The key '{key}' is written more than once at the top level.";
            }
        }

        if (trigger is not null && (trigger != ManualTrigger || text.CountKey(TriggerKey) == 1))
        {
            var error = text.SetScalar(TriggerKey, trigger, quoteWhenAdded: false, "name");
            if (error is not null)
            {
                return error;
            }
        }

        if (cron is not null)
        {
            var error = text.SetScalar(ScheduleKey, cron, quoteWhenAdded: true, TriggerKey, "name");
            if (error is not null)
            {
                return error;
            }
        }

        edited = text.ToString();
        return null;
    }

    /// <summary>The top-level mapping as the generic deserializer reads it; null with the reason when
    /// the YAML does not parse or is not one mapping.</summary>
    internal static Dictionary<object, object?>? Parse(string yaml, out string? error)
    {
        error = null;
        try
        {
            if (GenericDeserializer.Deserialize<object?>(yaml) is Dictionary<object, object?> map)
            {
                return map;
            }

            error = "The pipeline YAML does not parse as a mapping of keys.";
        }
        catch (Exception ex) when (ex is YamlException or ArgumentException or InvalidOperationException)
        {
            error = $"The pipeline YAML does not parse: {YamlParsingHelper.DescribeParseError(ex)}";
        }

        return null;
    }

    private static string? Verify(string edited, Dictionary<object, object?> expected)
    {
        var parsed = Parse(edited, out var error);
        if (parsed is null)
        {
            return $"The edited pipeline YAML was refused, nothing was written: {error}";
        }

        return YamlTree.AreEqual(expected, parsed)
            ? null
            : "The edit would have changed more than the schedule and the trigger, so it was refused.";
    }
}
