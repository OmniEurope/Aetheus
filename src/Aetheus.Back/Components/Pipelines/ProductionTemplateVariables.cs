// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>The versioned files a production deployment renders with the project's variables: the text of the environment
/// sample (null when the branch holds none) and of each Apache template.</summary>
internal sealed record ProductionTemplates(string? EnvSample, IReadOnlyList<string> ApacheTemplates);

/// <summary>
/// Recette R-66 and R-68: the variables the production setup creates are not only the ones the Compose file reads. The
/// repository's own environment sample (<c>.pipeline/configs/env/production.env</c>, <c>#{NAME}#</c> placeholders) and its
/// Apache templates (<c>.pipeline/configs/apache/*.conf</c>, which proxy to <c>#{PORT_FRONT}#</c> and
/// <c>#{PORT_BACK}#</c>) are rendered at each deployment, and a name they read that no library or vault holds stops the
/// deployment. The setup therefore plans them like a Compose variable: the convention fills what it knows, the port
/// registry reserves the ports, and the others are asked.
/// </summary>
internal static class ProductionTemplateVariables
{
    internal const string EnvDirectory = ".pipeline/configs/env";
    internal const string ApacheDirectory = ".pipeline/configs/apache";

    /// <summary>The project of the platform itself keeps its own pipelines and files: nothing is read from its templates.</summary>
    internal const string PlatformProjectSlug = "aetheus";

    /// <summary>The names the Apache templates read that are ports the setup reserves (<c>PORT_FRONT</c>, <c>PORT_BACK</c>).</summary>
    public static IReadOnlyList<string> ApachePorts(ProductionTemplates templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        return [.. templates.ApacheTemplates
            .SelectMany(PipelineConfigTemplateRenderer.FindTokenNames)
            .Where(name => name is ProductionVariableClassifier.FrontPort or ProductionVariableClassifier.BackPort)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The plans of <paramref name="plans"/> plus those of the names the repository's own templates read and the
    /// Compose file does not: an environment placeholder is classified as a Compose variable the file requires, an Apache
    /// port becomes a port to reserve. A name already planned (under its name or the name it travels under) is not planned
    /// twice. The platform's own project is left as it is.</summary>
    public static IReadOnlyList<ProductionVariablePlan> Extend(
        IReadOnlyList<ProductionVariablePlan> plans, ProductionTemplates templates, string slug)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(slug);
        if (string.Equals(slug, PlatformProjectSlug, StringComparison.OrdinalIgnoreCase)) return plans;

        var known = new HashSet<string>(plans.SelectMany(plan => new[] { plan.Name, plan.Key }), StringComparer.Ordinal);
        var added = new List<ProductionVariablePlan>();
        var envNames = templates.EnvSample is null ? [] : PipelineEnvTemplateRenderer.ReferencedNames(templates.EnvSample);
        var fromEnvironment = new List<ComposeVariable>();
        foreach (var name in envNames)
        {
            if (known.Contains(name) || known.Contains(AgentEnvironmentPolicy.StepSafeName(name))) continue;
            known.Add(name);
            fromEnvironment.Add(new ComposeVariable(name, ComposeVariableForm.Required, null));
        }
        added.AddRange(ProductionVariableClassifier.Classify(fromEnvironment, slug));
        added.AddRange(ApachePorts(templates).Where(known.Add)
            .Select(name => new ProductionVariablePlan(name, ProductionVariableKind.Port, false, null)));
        return added.Count == 0 ? plans : [.. plans, .. added];
    }
}
