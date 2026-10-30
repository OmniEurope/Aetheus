// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// R-82: where a deployment run put the application. Read, never guessed, from two facts the run
/// already holds: its own definition snapshot (a deploy step of the run completed) and the variables
/// the deployment itself was given (<c>BG_PUBLIC_ORIGIN</c> and <c>BG_PUBLIC_API_ORIGIN</c> for a
/// blue-green deployment, <c>PUBLIC_APP_URL</c> and <c>PUBLIC_API_URL</c> otherwise: the same
/// values the Probe step and the compose files read, resolved at launch with the project's libraries).
/// A run that did not deploy, or whose address is missing or not a plain http(s) address, has none.
/// </summary>
internal static class PipelineRunDeployedAddress
{
    private static readonly string[] AppVariables = ["BG_PUBLIC_ORIGIN", "PUBLIC_APP_URL"];
    private static readonly string[] ApiVariables = ["BG_PUBLIC_API_ORIGIN", "PUBLIC_API_URL"];

    public static PipelineRunDeployedAddressDto? Read(
        PipelineRun run, PipelineYamlDefinition? definition, IReadOnlyDictionary<string, string> variables)
    {
        if (run.Status != PipelineStatus.Success || definition is null) return null;
        if (!DeployedStepCompleted(run, definition)) return null;

        var appUrl = FirstAddress(variables, AppVariables);
        if (appUrl is null) return null;
        var apiUrl = FirstAddress(variables, ApiVariables);
        // The API gets a link of its own only when it answers elsewhere than the application.
        if (apiUrl is not null && string.Equals(apiUrl, appUrl, StringComparison.OrdinalIgnoreCase)) apiUrl = null;
        return new PipelineRunDeployedAddressDto { AppUrl = appUrl, ApiUrl = apiUrl };
    }

    /// <summary>A step that puts a release in service finished successfully: the blue-green cutover, a
    /// deploy task, or the release record marked deployed.</summary>
    private static bool DeployedStepCompleted(PipelineRun run, PipelineYamlDefinition definition)
    {
        var deploySteps = YamlParsingHelper.FlattenJobs(definition)
            .SelectMany(stage => stage.Steps.Where(IsDeployStep).Select(step => (Stage: stage.Name, Step: step.Name)))
            .ToHashSet();
        return run.StepRuns.Any(step =>
            step.Status == TaskExecutionStatus.Success
            && deploySteps.Contains((step.StageName, StepName(step))));
    }

    private static string StepName(PipelineStepRun step) =>
        step.MatrixLeg is null ? step.StepName : step.StepName.Replace($" [{step.MatrixLeg}]", string.Empty);

    private static bool IsDeployStep(PipelineStepDefinition step) =>
        string.Equals(step.Type, "bluegreen-switch", StringComparison.OrdinalIgnoreCase)
        || string.Equals(step.Type, "deploy", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(step.Type, "release", StringComparison.OrdinalIgnoreCase) && step.Deployed);

    private static string? FirstAddress(IReadOnlyDictionary<string, string> variables, string[] names)
    {
        foreach (var name in names)
        {
            if (!variables.TryGetValue(name, out var value)) continue;
            var address = value.Trim();
            // A placeholder that was never resolved is not an address.
            if (address.Contains("$(", StringComparison.Ordinal)) continue;
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host))
                return address.TrimEnd('/');
        }
        return null;
    }
}
