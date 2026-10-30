// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>What the YAML of a generated pipeline is built from: the wizard's choices, already resolved
/// (an option counts only when the type proposes it).</summary>
/// <param name="SourceBranch">R-94: the branch every pipeline reads, written as <c>source_branch:</c>; null writes none, and
/// the project's default branch then applies.</param>
/// <param name="Languages">R-105: the languages the quality pipeline analyses, written as the stages left out for the ones
/// not used; null writes none (the template's stages as they are).</param>
/// <param name="LintSourceStage">The "Lint source" stage of the pinned quality template without its unused step
/// (<see cref="PipelineSetupLanguages.RestatedLintSource"/>), needed when exactly one of Python and Java is used.</param>
/// <param name="RunDeployAfterCandidate">R-72: the candidate starts the deployment pipeline created in the same pass
/// (<c>on_success:</c>) only when this is true, which is the wizard's default.</param>
internal sealed record PipelineSetupYamlInput(
    string Prefix,
    (bool Frontend, bool Backend, bool Database) Parts,
    string AnalyticsPackageTemplate,
    bool Rollback,
    bool Quality,
    bool Security,
    bool Qa,
    bool QaExtended,
    bool SecurityHistory,
    IReadOnlyCollection<string> CreatedSuffixes,
    string? SourceBranch = null,
    PipelineSetupLanguages? Languages = null,
    string? LintSourceStage = null,
    bool RunDeployAfterCandidate = true);

/// <summary>
/// Writes the YAML of a pipeline the setup wizard creates: <c>extends: template@version</c> and the
/// variables the template reads. The point of this class is R2-11: an <c>APPLICATION_*_PIPELINE</c> variable
/// is written only for a pipeline the wizard really creates. A name written for a pipeline that does not
/// exist would send a candidate to trigger it and fail at the first run.
/// The wizard asks for no command (R2-25): the <c>APPLICATION_*_COMMANDS</c> variables are the template's own,
/// so a pipeline created here carries none and the template says so in its run when it has nothing to run.
/// </summary>
internal static class PipelineSetupYamlBuilder
{
    /// <param name="production">What the production setup produced, for the production pipeline only (ADR-057): the
    /// Compose file, the strategy and the server. Null writes none of it.</param>
    /// <param name="templateVersion">The numbered version the pipeline is pinned to: the latest published one at
    /// creation (user decision 2026-10-24). A generic template is never referenced as "latest" or without a number, so a
    /// version that is not a real number (0 from a catalog that did not carry it) is refused instead of written.</param>
    public static string Build(string pipelineName, string templateName, int templateVersion,
        PipelineSetupYamlInput input, PipelineSetupProductionInput? production = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(templateVersion, 1);
        var yaml = new StringBuilder();
        yaml.Append("name: ").Append(pipelineName).Append('\n');
        yaml.Append("extends: ").Append(templateName).Append('@').Append(templateVersion).Append('\n');
        yaml.Append("trigger: manual\n");
        if (!string.IsNullOrWhiteSpace(input.SourceBranch))
            yaml.Append("source_branch: \"").Append(Escape(input.SourceBranch)).Append("\"\n");
        var deployment = templateName is PipelineSetupRoles.DeployProdTemplate or PipelineSetupRoles.ReleaseFastTemplate
            ? production : null;
        AppendDeployServer(yaml, deployment);
        AppendDeploymentChain(yaml, templateName, input);
        yaml.Append("variables:\n");
        foreach (var (key, value) in Variables(input).Concat(ProductionVariables(templateName, production)))
            yaml.Append("  ").Append(key).Append(": \"").Append(Escape(value)).Append("\"\n");
        yaml.Append(DeliveryTemplateNames.Is(templateName, DeliveryTemplateNames.Quality) && input.Languages is { } languages
            ? languages.QualityStages(input.LintSourceStage)
            : "stages: []");
        return yaml.ToString();
    }

    /// <summary>R-72: a candidate that succeeds hands the release it published to the deployment pipeline the wizard
    /// creates in the same pass, as <c>aetheus-candidate</c> does by hand. Only the candidate template carries it, and only
    /// when that pipeline exists (a name written for a pipeline that does not exist would fail at the first success). The
    /// deployment keeps its own approvals, so production still waits for them. The user decides it (user decision
    /// 2026-10-25): the wizard's "Run the deployment after the candidate" option, ticked by default, writes nothing when
    /// it is not.</summary>
    private static void AppendDeploymentChain(StringBuilder yaml, string templateName, PipelineSetupYamlInput input)
    {
        if (!input.RunDeployAfterCandidate
            || !DeliveryTemplateNames.Is(templateName, DeliveryTemplateNames.Candidate)
            || !input.CreatedSuffixes.Contains(PipelineSetupRoles.DeployProd)) return;
        yaml.Append("on_success:\n");
        yaml.Append("  - pipeline: ").Append(input.Prefix).Append('-').Append(PipelineSetupRoles.DeployProd).Append('\n');
        yaml.Append("    release: latest\n");
    }

    /// <summary>R-21: the server the deployment runs on is the template's own parameter, written again here as a choice
    /// among the project's servers, with the server of its production environment as default. The servers come from the
    /// project and its environment, never from the Compose file (R-80), so they are written even when the repository
    /// could not be read. With no server known nothing is written: the template's parameter stays required, so the
    /// run asks for it before it starts.</summary>
    private static void AppendDeployServer(StringBuilder yaml, PipelineSetupProductionInput? production)
    {
        var servers = (production?.Servers ?? [])
            .Concat(production?.Server is { Length: > 0 } chosen ? [chosen] : [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (servers.Count == 0) return;
        yaml.Append("parameters:\n");
        yaml.Append("  - name: deployServer\n");
        yaml.Append("    display_name: Production server\n");
        yaml.Append("    display_name_fr: Serveur de production\n");
        yaml.Append("    description: Name of the project's server the deployment runs on.\n");
        yaml.Append("    description_fr: Nom du serveur du projet sur lequel le déploiement s'exécute.\n");
        yaml.Append("    type: choice\n");
        yaml.Append("    required: true\n");
        yaml.Append("    allowed_values:\n");
        foreach (var name in servers)
            yaml.Append("      - \"").Append(Escape(name)).Append("\"\n");
        if (production?.Server is { Length: > 0 } server)
            yaml.Append("    default: \"").Append(Escape(server)).Append("\"\n");
    }

    /// <summary>The production pipeline gets the Compose file, the strategy and the API; the QA pipeline the Compose
    /// file only, for its disposable stack (R-19).</summary>
    private static IEnumerable<(string Key, string Value)> ProductionVariables(
        string templateName, PipelineSetupProductionInput? production)
    {
        if (production is null) yield break;
        if (templateName == PipelineSetupRoles.QaTemplate)
        {
            if (!string.IsNullOrEmpty(production.ComposeFile)) yield return ("COMPOSE_FILE", production.ComposeFile);
            yield break;
        }
        if (templateName != PipelineSetupRoles.DeployProdTemplate) yield break;
        // Only what is known: the Compose file, the strategy and the API of a repository that was not read are not claimed.
        if (!string.IsNullOrEmpty(production.ComposeFile)) yield return ("COMPOSE_FILE", production.ComposeFile);
        if (!string.IsNullOrEmpty(production.Strategy)) yield return ("DEPLOY_STRATEGY", production.Strategy);
        if (production.HasApi is { } hasApi) yield return ("DEPLOY_HAS_API", Flag(hasApi));
    }

    /// <summary>The value of a double-quoted YAML scalar: a name typed in the project (a server) never ends it early.</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static IEnumerable<(string Key, string Value)> Variables(PipelineSetupYamlInput input)
    {
        yield return ("APPLICATION_PREFIX", input.Prefix);
        yield return ("APPLICATION_HAS_FRONTEND", Flag(input.Parts.Frontend));
        yield return ("APPLICATION_HAS_BACKEND", Flag(input.Parts.Backend));
        yield return ("APPLICATION_HAS_DATABASE", Flag(input.Parts.Database));
        yield return ("APPLICATION_ROLLBACK_ENABLED", Flag(input.Rollback));
        yield return ("APPLICATION_QUALITY_ENABLED", Flag(input.Quality));
        yield return ("APPLICATION_SECURITY_ENABLED", Flag(input.Security));
        yield return ("APPLICATION_QA_ENABLED", Flag(input.Qa));
        yield return ("APPLICATION_QA_EXTENDED_ENABLED", Flag(input.QaExtended));
        yield return ("APPLICATION_SECURITY_HISTORY_ENABLED", Flag(input.SecurityHistory));
        foreach (var (variable, role) in PipelineVariables(input.AnalyticsPackageTemplate))
            if (input.CreatedSuffixes.Contains(role))
                yield return (variable, $"{input.Prefix}-{role}");
    }

    /// <summary>The pipeline-name variables the templates read, with the suffix of the pipeline each names.</summary>
    private static (string Variable, string Role)[] PipelineVariables(string analyticsPackageTemplate) =>
    [
        ("APPLICATION_CI_PIPELINE", PipelineSetupRoles.Ci),
        ("APPLICATION_QUALITY_PIPELINE", PipelineSetupRoles.Quality),
        ("APPLICATION_SECURITY_PIPELINE", PipelineSetupRoles.Security),
        ("APPLICATION_QA_PIPELINE", PipelineSetupRoles.Qa),
        ("APPLICATION_QA_EXTENDED_PIPELINE", PipelineSetupRoles.QaExtended),
        ("APPLICATION_SECURITY_HISTORY_PIPELINE", PipelineSetupRoles.SecurityHistory),
        ("APPLICATION_CANDIDATE_PIPELINE", PipelineSetupRoles.Candidate),
        ("APPLICATION_DEPLOY_PIPELINE", PipelineSetupRoles.DeployProd),
        ("APPLICATION_PACKAGE_TELEMETRY_PIPELINE", PipelineSetupRoles.PackageTelemetry),
        ("APPLICATION_PACKAGE_DOTNET_ANALYTICS_PIPELINE", analyticsPackageTemplate),
        ("APPLICATION_PACKAGE_BROWSER_ANALYTICS_PIPELINE", PipelineSetupRoles.PackageWebAnalyticsBrowser)
    ];

    private static string Flag(bool value) => value ? "true" : "false";
}
