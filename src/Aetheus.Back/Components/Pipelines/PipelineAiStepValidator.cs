// SPDX-License-Identifier: EUPL-1.2

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
        if (!string.Equals(step.Type, "ai", StringComparison.OrdinalIgnoreCase))
        {
            // advisory exists to keep an opinion from deciding anything; on a step that produces
            // evidence or deploys it would hide a real failure, so it stays an AI-only key.
            if (step.Advisory || step.FindingsSarif is not null || step.CaptureDiff is not null)
                errors.Add($"Step '{step.Name}' in {context}: advisory, findings_sarif and capture_diff are only valid on an ai step.");
            return;
        }

        if (string.IsNullOrWhiteSpace(step.Profile))
            errors.Add($"AI step '{step.Name}' in {context} must select a profile.");
        if (string.IsNullOrWhiteSpace(step.Prompt) == string.IsNullOrWhiteSpace(step.PromptFile))
            errors.Add($"AI step '{step.Name}' in {context} must set exactly one of prompt or prompt_file.");
        if (step.PromptFile is not null && !IsSafeWorkspaceRelativePath(step.PromptFile))
            errors.Add($"AI step '{step.Name}' in {context} has an unsafe prompt_file.");
        if (step.ContextPaths.Count > 32
            || step.ContextPaths.Any(path => !IsSafeWorkspaceRelativePath(path)))
            errors.Add($"AI step '{step.Name}' in {context} has invalid context_paths.");
        if (step.FindingsSarif is not null && !IsSafeWorkspaceRelativePath(step.FindingsSarif))
            errors.Add($"AI step '{step.Name}' in {context} has an unsafe findings_sarif.");
        if (step.Advisory && step.Gate)
            errors.Add($"AI step '{step.Name}' in {context} cannot be both advisory and a gate: an advisory verdict blocks nothing.");
    }

    private static bool IsSafeWorkspaceRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Length > 260)
            return false;
        return !value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }
}
