// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// Says a refusal stated as <see cref="ProblemDto"/> codes in the user's language: resource
/// <c>LaunchProblem_{code}</c>, whose <c>{name}</c> placeholders take the problem's parameters. The
/// parameters are user and repository data and stay plain text (the toast renders text, never markup).
/// Returns null when there is nothing to translate or when one code is unknown to this front, so the
/// caller shows the English message of the API instead of half a translation.
/// </summary>
internal static class LaunchProblemText
{
    public const string KeyPrefix = "LaunchProblem_";

    /// <summary>The resource that puts a problem of a triggered pipeline after the chain reaching it.</summary>
    public const string InChainKey = "LaunchProblemInChain";

    public const string ChainParameter = "chain";

    private const string SourceParameter = "source";

    private const string SourceKeyPrefix = "LaunchProblemSource_";

    private static readonly Regex Placeholder = new(@"\{(?<name>[A-Za-z][A-Za-z0-9]*)\}", RegexOptions.CultureInvariant);

    public static string? Describe(IReadOnlyList<ProblemDto>? problems, IStringLocalizer<AppStrings> localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (problems is not { Count: > 0 }) return null;
        var sentences = new List<string>(problems.Count);
        foreach (var problem in problems)
        {
            if (Format(problem, localizer) is not { } sentence) return null;
            sentences.Add(sentence);
        }
        return string.Join(" ", sentences);
    }

    /// <summary>A problem stored as a string (<see cref="StoredProblem"/>: a warning of a run, an advisory warning,
    /// the detail of a preflight check) in the user's language. A string that is not a stored problem (a warning
    /// stored before problems had codes, a free sentence) and a stored problem this front does not know are
    /// returned as they are: the English sentence is shown rather than nothing.</summary>
    public static string DescribeStored(string? stored, IStringLocalizer<AppStrings> localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (stored is null) return string.Empty;
        if (!StoredProblem.TryRead(stored, out var content)) return stored;
        return Format(content.Problem, localizer, content.Inner) ?? content.English;
    }

    /// <summary>One problem in the user's language, or null when its code has no resource.</summary>
    public static string? Format(ProblemDto problem, IStringLocalizer<AppStrings> localizer) =>
        Format(problem, localizer, null);

    /// <summary>One problem in the user's language; <paramref name="inner"/> are the problems that explain it,
    /// said first and written where the sentence names <c>{detail}</c>. Null when its code, or one of the
    /// inner codes, has no resource.</summary>
    public static string? Format(ProblemDto problem, IStringLocalizer<AppStrings> localizer, IReadOnlyList<ProblemDto>? inner)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(localizer);
        if (string.IsNullOrWhiteSpace(problem.Code)) return null;
        var template = localizer[KeyPrefix + problem.Code];
        if (template.ResourceNotFound) return null;
        var parameters = problem.Parameters ?? [];
        if (inner is { Count: > 0 })
        {
            var detail = Describe(inner, localizer);
            if (detail is null) return null;
            parameters = new Dictionary<string, string>(parameters, StringComparer.Ordinal) { ["detail"] = detail };
        }
        // One pass over the template: a value that itself contains "{x}" is never substituted again.
        var sentence = Placeholder.Replace(
            template.Value,
            match => parameters.TryGetValue(match.Groups["name"].Value, out var value)
                ? Localized(match.Groups["name"].Value, value, localizer)
                : match.Value);
        return parameters.TryGetValue(ChainParameter, out var chain) && !string.IsNullOrWhiteSpace(chain)
            ? Placeholder.Replace(
                localizer[InChainKey].Value,
                match => match.Groups["name"].Value switch
                {
                    "chain" => chain,
                    "problem" => sentence,
                    _ => match.Value
                })
            : sentence;
    }

    /// <summary>The automated launch sources the back names with a fixed word ("Scheduler", "Webhook") are said in
    /// the user's language (resource <c>LaunchProblemSource_{value}</c>); any other value is data and stays as it is.</summary>
    private static string Localized(string name, string value, IStringLocalizer<AppStrings> localizer)
    {
        if (name != SourceParameter) return value;
        var word = localizer[SourceKeyPrefix + value];
        return word.ResourceNotFound ? value : word.Value;
    }
}
