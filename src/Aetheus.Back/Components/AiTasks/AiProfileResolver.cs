// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AiTasks;

/// <summary>How a pipeline AI step got (or failed to get) its profile: the chosen profile, why it was
/// chosen, and, when none can run, why each candidate was refused.</summary>
/// <param name="Profile">The profile the step runs with; null when none can run.</param>
/// <param name="Problem">Why no profile can run; null when <paramref name="Profile"/> is set.</param>
/// <param name="Reason">One human sentence naming the profile and why it was picked. Written to the step
/// log, never silent. When no profile can run it is the same text as <paramref name="Problem"/>.</param>
/// <param name="IsFallback">True when the step's own <c>profile:</c> did not decide (it named none, or
/// none that can run): the choice is then worth showing to the person who wrote the step.</param>
internal sealed record AiProfileSelection(
    AiRunnerProfile? Profile, string? Problem, string Reason, bool IsFallback);

/// <summary>
/// Picks the AI runner profile of a pipeline <c>ai</c> step from the profiles of the pipeline's
/// organization. A step used to fail on a profile name nobody had created; it now takes an available
/// profile, in this order:
/// <list type="number">
/// <item>the step's <c>profile:</c> name, when a profile of that name exists and can run;</item>
/// <item>the pipeline's assigned profile, when set and able to run;</item>
/// <item>the organization's profile able to run with the lowest <see cref="AiRunnerProfile.Priority"/>
/// (a tie goes to the lowest id).</item>
/// </list>
/// "Able to run" is <see cref="AiProfileRuntime.Problem"/> being null. Only the profiles it is given are
/// considered: the caller passes the pipeline's own organization, so another organization's profile is
/// never picked, whatever its name or id.
/// </summary>
internal static class AiProfileResolver
{
    public static AiProfileSelection Resolve(
        string? requestedName, int? pipelineProfileId, IReadOnlyCollection<AiRunnerProfile> organizationProfiles)
    {
        ArgumentNullException.ThrowIfNull(organizationProfiles);
        var ordered = organizationProfiles.OrderBy(profile => profile.Priority).ThenBy(profile => profile.Id).ToList();
        var skipped = new List<string>();

        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            var name = requestedName.Trim();
            var named = FindByName(ordered, name);
            if (named is null)
                skipped.Add($"Profile '{name}' named by the step was not found in the organization.");
            else if (AiProfileRuntime.Problem(named) is { } namedProblem)
                skipped.Add($"Profile '{named.Name}' named by the step cannot be used: {namedProblem}");
            else
                return Chosen(named, $"Using AI runner profile '{named.Name}' named by the step.", false, skipped);
        }

        if (pipelineProfileId is { } pipelineProfileIdValue)
        {
            var assigned = ordered.FirstOrDefault(profile => profile.Id == pipelineProfileIdValue);
            if (assigned is null)
                skipped.Add("The AI runner profile assigned to the pipeline is not in the pipeline's organization.");
            else if (AiProfileRuntime.Problem(assigned) is { } assignedProblem)
                skipped.Add($"The pipeline's profile '{assigned.Name}' cannot be used: {assignedProblem}");
            else
                return Chosen(assigned, $"Using the pipeline's AI runner profile '{assigned.Name}'.", true, skipped);
        }

        var automatic = ordered.FirstOrDefault(profile => AiProfileRuntime.Problem(profile) is null);
        if (automatic is not null)
            return Chosen(
                automatic,
                $"Using AI runner profile '{automatic.Name}' (priority {automatic.Priority.ToString(CultureInfo.InvariantCulture)}, "
                + "the most preferred usable profile of the organization).",
                true, skipped);

        return None(ordered, skipped);
    }

    /// <summary>The exact name first, then the same name in another case (the unique index is case
    /// sensitive, so both can exist); the most preferred one wins among several.</summary>
    private static AiRunnerProfile? FindByName(List<AiRunnerProfile> ordered, string name) =>
        ordered.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.Ordinal))
        ?? ordered.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));

    private static AiProfileSelection Chosen(
        AiRunnerProfile profile, string reason, bool isFallback, List<string> skipped) =>
        new(profile, null, skipped.Count == 0 ? reason : $"{string.Join(' ', skipped)} {reason}", isFallback);

    private static AiProfileSelection None(List<AiRunnerProfile> ordered, List<string> skipped)
    {
        var details = new List<string>(skipped);
        if (ordered.Count == 0)
            details.Add("The pipeline's organization has no AI runner profile.");
        else
            details.AddRange(ordered.Select(profile => AiProfileRuntime.Problem(profile)!));
        var problem = "No AI runner profile can run this step. " + string.Join(' ', details.Distinct(StringComparer.Ordinal));
        return new AiProfileSelection(null, problem, problem, true);
    }
}
