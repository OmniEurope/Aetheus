// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

internal static class PipelineStepAnalysisValidator
{
    public static void Validate(
        ICollection<string> errors,
        PipelineStepDefinition step,
        string context,
        string? environment)
    {
        var isAnalysisGate = string.Equals(
            step.Type, "analysis-gate", StringComparison.OrdinalIgnoreCase);
        if (isAnalysisGate)
            ValidateGate(errors, step, context);
        else if (!string.IsNullOrWhiteSpace(step.AnalysisScope)
                 || !string.IsNullOrWhiteSpace(step.AnalysisPreset)
                 || step.AnalysisRules.Count > 0
                 || step.AnalysisGrading is not null)
        {
            errors.Add($"Step '{step.Name}' in {context} analysis gate fields require type analysis-gate.");
        }

        if (!string.IsNullOrWhiteSpace(step.AnalysisCategory))
        {
            if (!string.Equals(step.Type, "lint", StringComparison.OrdinalIgnoreCase))
                errors.Add($"Step '{step.Name}' in {context} analysis_category is only valid on a lint step.");
            else if (!LintAnalysisCategories.TryParse(step.AnalysisCategory, out _))
                errors.Add($"Step '{step.Name}' in {context} analysis_category must be code-quality, accessibility or container.");
        }

        if (!string.Equals(step.Type, "scanner", StringComparison.OrdinalIgnoreCase)) return;
        ValidateScanner(errors, step, context, environment);
    }

    private static void ValidateGate(
        ICollection<string> errors,
        PipelineStepDefinition step,
        string context)
    {
        if (!AnalysisGateScopes.IsValid(step.AnalysisScope))
            errors.Add($"Step '{step.Name}' in {context} analysis_scope must be security or quality.");
        foreach (var error in PipelineAnalysisGateRuleValidator.Validate(
                     step.AnalysisScope, step.AnalysisPreset, step.AnalysisRules))
            errors.Add($"Step '{step.Name}' in {context} {error}");
        foreach (var error in PipelineAnalysisGradingValidator.Validate(
                     step.AnalysisScope, step.AnalysisGrading))
            errors.Add($"Step '{step.Name}' in {context} {error}");
    }

    private static void ValidateScanner(
        ICollection<string> errors,
        PipelineStepDefinition step,
        string context,
        string? environment)
    {
        var scanner = string.IsNullOrWhiteSpace(step.Scanner) ? null : ScannerManifestCatalog.Find(step.Scanner);
        if (scanner is null)
        {
            errors.Add($"Step '{step.Name}' in {context} requires a scanner key from scanner-manifest.json.");
            return;
        }

        if (!scanner.Key.StartsWith("zap-", StringComparison.OrdinalIgnoreCase))
        {
            if (HasDastFields(step))
                errors.Add($"Step '{step.Name}' in {context} DAST target and API specification fields are reserved for DAST scanners.");
            return;
        }
        ValidateDastScanner(errors, step, context, environment, scanner.Key);
    }

    private static void ValidateDastScanner(
        ICollection<string> errors,
        PipelineStepDefinition step,
        string context,
        string? environment,
        string scannerKey)
    {
        if ((!Uri.TryCreate(step.TargetUrl, UriKind.Absolute, out var target)
            || target.Scheme is not ("http" or "https")) && !IsVariableReference(step.TargetUrl))
            errors.Add($"Step '{step.Name}' in {context} requires an absolute HTTP(S) target_url.");
        if (string.IsNullOrWhiteSpace(environment))
            errors.Add($"Step '{step.Name}' in {context} requires a named Aetheus environment for the trusted DAST policy.");
        if (!string.IsNullOrWhiteSpace(step.TargetClassification))
            errors.Add($"Step '{step.Name}' in {context} must not self-declare target_classification; configure DAST on the Aetheus environment.");
        if (step.Active && !string.Equals(scannerKey, "zap-active", StringComparison.OrdinalIgnoreCase))
            errors.Add($"Step '{step.Name}' in {context} active DAST requires the zap-active scanner.");
        if (string.Equals(scannerKey, "zap-api", StringComparison.OrdinalIgnoreCase))
            ValidateApiScanner(errors, step, context);
        else if (!string.IsNullOrWhiteSpace(step.ApiSpecificationUrl)
                 || step.ApiSpecificationUrls.Count > 0
                 || !string.IsNullOrWhiteSpace(step.ApiSpecificationFormat))
        {
            errors.Add($"Step '{step.Name}' in {context} API specification fields are reserved for zap-api.");
        }
    }

    private static bool HasDastFields(PipelineStepDefinition step) =>
        !string.IsNullOrWhiteSpace(step.TargetUrl)
        || step.Active
        || !string.IsNullOrWhiteSpace(step.ApiSpecificationUrl)
        || step.ApiSpecificationUrls.Count > 0
        || !string.IsNullOrWhiteSpace(step.ApiSpecificationFormat);

    /// <summary>The most definitions one zap-api step scans in sequence.</summary>
    internal const int MaxApiSpecificationUrls = 16;

    private static void ValidateApiScanner(
        ICollection<string> errors,
        PipelineStepDefinition step,
        string context)
    {
        // One definition, or a list scanned in sequence by the same step; never both.
        if (step.ApiSpecificationUrls.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(step.ApiSpecificationUrl))
                errors.Add($"Step '{step.Name}' in {context} sets both api_specification_url and api_specification_urls; keep one.");
            if (step.ApiSpecificationUrls.Count > MaxApiSpecificationUrls)
                errors.Add($"Step '{step.Name}' in {context} lists more than {MaxApiSpecificationUrls} api_specification_urls.");
            if (step.ApiSpecificationUrls.Any(url => !IsHttpsOrVariable(url)))
                errors.Add($"Step '{step.Name}' in {context} requires every api_specification_urls entry to be an HTTPS URL.");
        }
        else if (!IsHttpsOrVariable(step.ApiSpecificationUrl))
            errors.Add($"Step '{step.Name}' in {context} requires an HTTPS api_specification_url.");
        if (step.ApiSpecificationFormat?.ToLowerInvariant() is not ("openapi" or "graphql"))
            errors.Add($"Step '{step.Name}' in {context} api_specification_format must be openapi or graphql.");
    }

    private static bool IsHttpsOrVariable(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var specification) && specification.Scheme == Uri.UriSchemeHttps
        || IsVariableReference(value);

    private static bool IsVariableReference(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith("$(", StringComparison.Ordinal)
        && value.EndsWith(')')
        && value.Length > 3;
}
