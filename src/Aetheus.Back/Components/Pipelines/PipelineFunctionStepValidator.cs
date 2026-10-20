// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Validates the function contract of a typed <c>type: aetheus</c> step against
/// <see cref="PipelineFunctionCatalog"/>, so a misspelled function or parameter is refused at save
/// time instead of failing on an agent.
/// </summary>
internal static class PipelineFunctionStepValidator
{
    public static void Validate(List<string> errors, PipelineStepDefinition step, string context)
    {
        if (!string.Equals(step.Type, PipelineFunctionCatalog.StepType, StringComparison.OrdinalIgnoreCase))
        {
            ValidateForeignStep(errors, step, context);
            return;
        }

        if (string.IsNullOrWhiteSpace(step.Function))
        {
            errors.Add($"Aetheus step '{step.Name}' in {context} must name a function.");
            return;
        }

        var function = PipelineFunctionCatalog.Find(step.Function);
        if (function is null)
        {
            errors.Add(UnknownFunctionMessage(step, context));
            return;
        }

        ValidateRequiredParameters(errors, step, context, function);
        ValidateUnknownParameters(errors, step, context, function);
    }

    private static void ValidateForeignStep(List<string> errors, PipelineStepDefinition step, string context)
    {
        if (step.Function is not null || step.With.Count > 0)
            errors.Add($"Step '{step.Name}' in {context}: function and with are only valid on a type: {PipelineFunctionCatalog.StepType} step.");
    }

    private static void ValidateRequiredParameters(
        List<string> errors, PipelineStepDefinition step, string context, PipelineFunctionDescriptor function)
    {
        foreach (var parameter in function.Parameters.Where(parameter => parameter.Required))
            if (!step.With.TryGetValue(parameter.Name, out var value) || string.IsNullOrWhiteSpace(value))
                errors.Add($"Aetheus step '{step.Name}' in {context}: function '{function.Name}' requires the with parameter '{parameter.Name}'.");
    }

    private static void ValidateUnknownParameters(
        List<string> errors, PipelineStepDefinition step, string context, PipelineFunctionDescriptor function)
    {
        foreach (var key in step.With.Keys.Where(key => function.FindParameter(key) is null))
            errors.Add($"Aetheus step '{step.Name}' in {context}: function '{function.Name}' has no with parameter '{key}'.");
    }

    private static string UnknownFunctionMessage(PipelineStepDefinition step, string context) =>
        $"Aetheus step '{step.Name}' in {context} names the unknown function '{step.Function}'; known functions: " +
        string.Join(", ", PipelineFunctionCatalog.All.Select(function => function.Name)) + ".";
}
