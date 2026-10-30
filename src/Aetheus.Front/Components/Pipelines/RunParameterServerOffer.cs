// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R-76: the production server a deployment runs on is asked as a text field when the pipeline was written without a
/// server (the setup wizard writes a choice among the project's servers, with the production environment's as default, as soon as
/// it knows one). The launch dialog then offers the servers the project has, the way the wizard would have, so nobody types a
/// server name: a choice among them, with the production environment's server (else the only one) preselected. Nothing is
/// guessed among several servers, and a parameter that already is a choice or has a default is left alone.
/// </summary>
internal static class RunParameterServerOffer
{
    /// <summary>The production template's parameter that names the server every stage working on the host runs on.</summary>
    internal const string ParameterName = "deployServer";

    /// <summary>The names the project's production environment goes by, as the production setup reads them.</summary>
    private static readonly string[] ProductionEnvironmentNames = ["prod", "production"];

    internal static bool IsAwaitingServer(PipelineRunParameterDto parameter) =>
        parameter.Name == ParameterName
        && parameter.Required
        && string.IsNullOrWhiteSpace(parameter.Default)
        && parameter.AllowedValues.Count == 0
        && (string.IsNullOrEmpty(parameter.Type) || parameter.Type.Equals("string", StringComparison.OrdinalIgnoreCase));

    internal static bool IsProductionEnvironment(string name) =>
        ProductionEnvironmentNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The same parameters, the server one turned into a choice among <paramref name="servers"/> (the project's own and
    /// those of its production environment), unchanged when there is none to offer.</summary>
    internal static List<PipelineRunParameterDto> Apply(
        List<PipelineRunParameterDto> declared, IEnumerable<string?> projectServers, IReadOnlyCollection<string?> productionServers)
    {
        var production = Names(productionServers);
        var names = Names(projectServers.Concat(production)).Order(StringComparer.Ordinal).ToList();
        if (names.Count == 0) return declared;

        // The server of the production environment when it has exactly one, else the project's only server.
        var preselected = production.Count == 1 ? production[0]
            : production.Count == 0 && names.Count == 1 ? names[0]
            : null;
        return
        [
            .. declared.Select(parameter => IsAwaitingServer(parameter)
                ? parameter with { Type = "choice", AllowedValues = names, Default = preselected }
                : parameter)
        ];
    }

    private static List<string> Names(IEnumerable<string?> names) =>
        [.. names.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).Distinct(StringComparer.Ordinal)];
}
