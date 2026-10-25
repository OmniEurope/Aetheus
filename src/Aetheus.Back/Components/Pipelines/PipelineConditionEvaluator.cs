// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>What a stage or step condition decided. <see cref="Error"/> is set when the condition is
/// not one of the supported forms: the engine then fails the stage or step, it never reads that as
/// met or as not met.</summary>
public readonly record struct PipelineConditionVerdict(bool Met, string? Error)
{
    public bool IsInvalid => Error is not null;

    internal static PipelineConditionVerdict Of(bool met) => new(met, null);

    internal static PipelineConditionVerdict Invalid(string error) => new(false, error);
}

/// <summary>
/// Stage and step conditions. A condition is exactly ONE supported form, matched on the whole text:
/// a status function (<c>always()</c>, <c>succeeded()</c>, <c>failed()</c>, <c>cancelled()</c>) or a
/// variable comparison (<c>eq</c>, <c>ne</c>, <c>contains</c> on <c>variables['NAME']</c> and a quoted
/// value). Anything else (<c>and(...)</c>, <c>or(...)</c>, <c>not(...)</c>, an unknown function, text
/// after a valid form) is refused when the definition is validated and fails the run if it is ever
/// evaluated. The former patterns matched anywhere in the text, so <c>or(eq(A), eq(B))</c> was read as
/// its first <c>eq</c> alone, silently, on conditions that gate approvals, rollbacks and deployments.
/// <c>partial</c> only for the <c>[GeneratedRegex]</c> source generator.
/// </summary>
public static partial class PipelineConditionEvaluator
{
    internal const string SupportedForms =
        "always(), succeeded(), failed(), cancelled(), eq(variables['NAME'], 'value'), "
        + "ne(variables['NAME'], 'value') or contains(variables['NAME'], 'value')";

    // Anchored on both ends: the whole condition is one comparison, nothing before, nothing after.
    [GeneratedRegex(
        @"\A\s*(eq|ne|contains)\s*\(\s*variables\['(\w+)'\]\s*,\s*'([^']*)'\s*\)\s*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VariableComparisonPattern();

    /// <summary>Evaluates a condition. An empty condition is met; an unsupported one is invalid.</summary>
    public static PipelineConditionVerdict Evaluate(
        string? condition, IReadOnlyDictionary<string, string> variables, bool previousStageFailed)
    {
        if (string.IsNullOrWhiteSpace(condition)) return PipelineConditionVerdict.Of(true);

        switch (condition.Trim().ToLowerInvariant())
        {
            case "always()": return PipelineConditionVerdict.Of(true);
            case "succeeded()": return PipelineConditionVerdict.Of(!previousStageFailed);
            case "failed()": return PipelineConditionVerdict.Of(previousStageFailed);
            case "cancelled()": return PipelineConditionVerdict.Of(false);
        }

        var match = VariableComparisonPattern().Match(condition);
        return match.Success
            ? PipelineConditionVerdict.Of(Compare(
                match.Groups[1].Value.ToLowerInvariant(), match.Groups[2].Value, match.Groups[3].Value, variables))
            : PipelineConditionVerdict.Invalid(DescribeUnsupported(condition));
    }

    /// <summary>Null when the condition is empty or supported, otherwise why it is refused.</summary>
    public static string? FindSyntaxError(string? condition) =>
        Evaluate(condition, new Dictionary<string, string>(), previousStageFailed: false).Error;

    /// <summary>Every unsupported condition of a definition: stages, jobs and steps, each named.</summary>
    public static IReadOnlyList<string> FindDefinitionProblems(PipelineYamlDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var problems = new List<string>();
        foreach (var stage in definition.Stages)
        {
            AddProblem(problems, $"Stage '{stage.Name}'", stage.Condition);
            foreach (var job in stage.Jobs)
                AddProblem(problems, $"Job '{job.Name}' in stage '{stage.Name}'", job.Condition);
        }
        foreach (var stage in YamlParsingHelper.FlattenJobs(definition))
            foreach (var step in stage.Steps)
                AddProblem(problems, $"Step '{step.Name}' in stage '{stage.Name}'", step.Condition);
        return problems;
    }

    private static void AddProblem(List<string> problems, string owner, string? condition)
    {
        if (FindSyntaxError(condition) is { } error)
            problems.Add($"{owner}: {error}");
    }

    // Same semantics as before for every supported form: an absent variable never equals nor contains
    // a value, and is always different from it (ne is met when the variable is absent).
    private static bool Compare(
        string function, string name, string expected, IReadOnlyDictionary<string, string> variables)
    {
        var present = variables.TryGetValue(name, out var actual);
        return function switch
        {
            "eq" => present && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "ne" => !present || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            _ => present && actual!.Contains(expected, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string DescribeUnsupported(string condition) =>
        $"condition '{PipelineConditionEvidence.CompactCondition(condition.Trim())}' is not supported. "
        + $"A condition is exactly one of {SupportedForms}; and(), or(), not(), other functions and "
        + "text after the condition are refused.";
}
