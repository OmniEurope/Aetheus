// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Pipelines;

/// <summary>Defines lint SARIF categories: code quality by default, accessibility for axe-core E2E reports (both in
/// the quality gate scope), or container for the SARIF of an image finding such as the .NET runtime CVEs
/// (<c>scan-dotnet-runtime-cve</c>), which belongs to the security gate scope.</summary>
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
            case "container":
                category = AnalysisCategory.Container;
                return true;
            default:
                category = AnalysisCategory.CodeQuality;
                return false;
        }
    }
}
