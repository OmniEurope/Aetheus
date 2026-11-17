// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// The reason of a refused automated launch in the recipient's language, worded from the problem codes of the
/// payload (<c>ReasonProblems</c>: code and parameters, as the run page reads them) and the notification
/// resources <c>LaunchProblem_{code}</c>. A payload stored before the codes existed, or a code this mail has no
/// word for, gives null: the mail then shows the English <c>Reason</c> as it always did.
/// </summary>
internal static partial class LaunchRefusedReasonText
{
    public const string ProblemsProperty = "ReasonProblems";

    private const string KeyPrefix = "LaunchProblem_";

    [GeneratedRegex(@"\{(?<name>[A-Za-z][A-Za-z0-9]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    public static string? Build(JsonElement? root, string language)
    {
        if (root is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty(ProblemsProperty, out var problems) || problems.ValueKind != JsonValueKind.Array)
            return null;
        var sentences = new List<string>();
        foreach (var problem in problems.EnumerateArray())
        {
            if (Sentence(problem, language) is not { } sentence) return null;
            sentences.Add(sentence);
        }
        return sentences.Count == 0 ? null : string.Join(" ", sentences);
    }

    private static string? Sentence(JsonElement problem, string language)
    {
        if (problem.ValueKind != JsonValueKind.Object
            || !problem.TryGetProperty("code", out var code) && !problem.TryGetProperty("Code", out code)
            || code.ValueKind != JsonValueKind.String || code.GetString() is not { Length: > 0 } name
            || NotificationEmailText.Find(KeyPrefix + name, language) is not { } template)
            return null;
        var parameters = problem.TryGetProperty("parameters", out var found) || problem.TryGetProperty("Parameters", out found)
            ? found : default;
        // One pass over the template: a value that itself contains "{x}" is never substituted again.
        return Placeholder().Replace(template, match =>
            parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty(match.Groups["name"].Value, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : match.Value);
    }
}
