// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Recette R-63: the production server a deployment runs on (<c>deployServer</c>) is, when the pipeline names none, the
/// single server of the project's production environment, resolved when the pipeline is read for a launch rather than
/// only written into its YAML by the wizard. The environment is often created after the pipeline, and the deployment
/// chained from a candidate carries no parameter of its own, so a default written once at creation left the launch
/// asking for a server (and the preview announcing that no agent was online) while the server was reachable. Two
/// servers in the environment are not a default: nothing is guessed among several.
/// </summary>
internal static class PipelineProductionServerDefault
{
    /// <summary>The production template's parameter that names the server every stage working on the host runs on.</summary>
    internal const string ParameterName = "deployServer";

    /// <summary>The names the project's production environment goes by, as the production setup reads them.</summary>
    private static readonly string[] ProductionEnvironmentNames = ["prod", "production"];

    /// <summary><paramref name="definition"/> with the server of the project's production environment as the default of its
    /// <c>deployServer</c> parameter; unchanged when the pipeline has no such parameter, already has a default, belongs to
    /// no project or the project has no production environment with exactly one server. A parameter that is a choice
    /// gains the server among its allowed values, so the default is a value it accepts.</summary>
    public static async Task<PipelineYamlDefinition> ApplyAsync(
        IPipelineEnvironmentReader? environments, PipelineYamlDefinition definition, int? projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (environments is null || projectId is not { } project) return definition;
        var index = definition.Parameters.FindIndex(parameter =>
            string.Equals(parameter.Name, ParameterName, StringComparison.Ordinal));
        if (index < 0 || !string.IsNullOrWhiteSpace(definition.Parameters[index].Default)) return definition;

        var server = await FindServerAsync(environments, project, ct).ConfigureAwait(false);
        if (server is null) return definition;

        var parameter = definition.Parameters[index];
        var allowed = parameter.AllowedValues.Count == 0 || parameter.AllowedValues.Contains(server, StringComparer.Ordinal)
            ? parameter.AllowedValues
            : [.. parameter.AllowedValues, server];
        var parameters = new List<PipelineTemplateParameterDefinition>(definition.Parameters)
        {
            [index] = parameter with { Default = server, AllowedValues = allowed }
        };
        return definition with { Parameters = parameters };
    }

    private static async Task<string?> FindServerAsync(IPipelineEnvironmentReader environments, int projectId, CancellationToken ct)
    {
        foreach (var name in ProductionEnvironmentNames)
        {
            var environment = await environments.FindEnvironmentByNameAsync(name, projectId, ct).ConfigureAwait(false);
            // A shared environment of the same name is not the project's own production.
            if (environment?.ProjectId != projectId) continue;
            var names = environment.Servers
                .Where(link => link.Server is not null)
                .Select(link => link.Server!.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return names.Count == 1 ? names[0] : null;
        }
        return null;
    }
}
