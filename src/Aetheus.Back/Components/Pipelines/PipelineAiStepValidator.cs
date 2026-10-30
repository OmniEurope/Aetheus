// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AiTasks;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Validates the AI-specific contract of a typed pipeline step.
/// </summary>
internal static class PipelineAiStepValidator
{
    public static void Validate(
        List<string> errors,
        PipelineStepDefinition step,
        string context)
    {
        // The CLI runner is gone (ADR-023, amendment (b) of 2026-10-11): the model writes no file and
        // changes nothing, so these keys would be silently ignored. Refusing them says so.
        if (step.FindingsSarif is not null || step.CaptureDiff is not null)
            errors.Add(RetiredKeysMessage(step, context));
        if (!string.Equals(step.Type, "ai", StringComparison.OrdinalIgnoreCase))
        {
            // advisory exists to keep an opinion from deciding anything; on a step that produces
            // evidence or deploys it would hide a real failure, so it stays an AI-only key.
            if (step.Advisory || step.Findings || step.GitRange is not null)
                errors.Add($"Step '{step.Name}' in {context}: advisory, findings and git_range are only valid on an ai step.");
            return;
        }
        ValidateAiStep(errors, step, context);
    }

    private static void ValidateAiStep(List<string> errors, PipelineStepDefinition step, string context)
    {
        // profile is optional: the run falls back on the pipeline's profile, then on the most preferred
        // usable profile of the organization (AiProfileResolver).
        if (string.IsNullOrWhiteSpace(step.Prompt) == string.IsNullOrWhiteSpace(step.PromptFile))
            errors.Add($"AI step '{step.Name}' in {context} must set exactly one of prompt or prompt_file.");
        if (step.PromptFile is not null && !IsSafeWorkspaceRelativePath(step.PromptFile))
            errors.Add($"AI step '{step.Name}' in {context} has an unsafe prompt_file.");
        if (!AiRepositoryContextRules.AreValidContextPaths(step.ContextPaths))
            errors.Add($"AI step '{step.Name}' in {context} has invalid context_paths.");
        if (step.GitRange is not null && !AiRepositoryContextRules.IsValidGitRange(step.GitRange))
            errors.Add($"AI step '{step.Name}' in {context} has an empty or oversized git_range.");
        if (step.Advisory && step.Gate)
            errors.Add($"AI step '{step.Name}' in {context} cannot be both advisory and a gate: an advisory verdict blocks nothing.");
    }

    private static string RetiredKeysMessage(PipelineStepDefinition step, string context) =>
        $"Step '{step.Name}' in {context}: findings_sarif and capture_diff were retired with the AI CLI runner; use findings: true.";

    private static bool IsSafeWorkspaceRelativePath(string value) =>
        AiRepositoryContextRules.IsSafeWorkspaceRelativePath(value);
}
