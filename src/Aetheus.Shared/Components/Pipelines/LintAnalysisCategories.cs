// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>Defines lint SARIF categories: code quality by default, or accessibility for axe-core
/// E2E reports. Both belong to the quality gate scope.</summary>
public static class LintAnalysisCategories
{
    /// <summary>The task variable carrying the category from the control plane to the agent.</summary>
    public const string VariableName = "AETHEUS_LINT_CATEGORY";

    public static bool TryParse(string? value, out AnalysisCategory category)
    {
        switch (value?.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
                    .Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant())
        {
            case null or "" or "codequality":
                category = AnalysisCategory.CodeQuality;
                return true;
            case "accessibility":
                category = AnalysisCategory.Accessibility;
                return true;
            default:
                category = AnalysisCategory.CodeQuality;
                return false;
        }
    }
}
