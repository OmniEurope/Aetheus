// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.Shared;

namespace Aetheus.Front.Components.Notifications;

/// <summary>
/// The reason of a refused automated launch, shown in the user's language in the inbox. The payload carries it
/// twice: <c>Reason</c>, the English sentence, and <c>ReasonProblems</c>, the same refusal as problem codes that
/// <see cref="LaunchProblemText"/> says (the mechanism of the run page). A notification stored before the codes
/// existed, or one whose code this front does not know, keeps its English <c>Reason</c> as it is.
/// </summary>
internal static class NotificationReasonText
{
    private const string ReasonName = "Reason";
    private const string ProblemsName = "ReasonProblems";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The payload elements with the reason said in the user's language and the codes behind it removed.</summary>
    public static IReadOnlyList<(string Name, string Value)> Apply(
        string eventType, IReadOnlyList<(string Name, string Value)> elements, IStringLocalizer<AppStrings> localizer)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(localizer);
        if (eventType != NotificationEventTypes.PipelineLaunchRefused) return elements;
        var said = Say(elements.FirstOrDefault(element => element.Name == ProblemsName).Value, localizer);
        var shown = new List<(string Name, string Value)>(elements.Count);
        foreach (var element in elements)
        {
            if (element.Name == ProblemsName) continue;
            shown.Add(element.Name == ReasonName && said is not null ? (element.Name, said) : element);
        }
        return shown;
    }

    private static string? Say(string? problemsJson, IStringLocalizer<AppStrings> localizer)
    {
        if (string.IsNullOrWhiteSpace(problemsJson)) return null;
        try
        {
            return LaunchProblemText.Describe(JsonSerializer.Deserialize<List<ProblemDto>>(problemsJson, Options), localizer);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
