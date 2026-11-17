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
/// <param name="External">R-34: the application is an external repository, whose quality pipeline restores no CI evidence
/// and, for a language the default quality commands do not cover (they run the .NET tests), declares no command.</param>
/// <param name="RunDeployAfterCandidate">R-72: the candidate starts the deployment pipeline created in the same pass
/// (<c>on_success:</c>) only when this is true, which is the wizard's default.</param>
/// <param name="ImageArchives">R-5: the <c>.tar.gz</c> archives the project's CI delivers, for the security pipeline only. Not
/// null and not empty, the pipeline is written with <c>APPLICATION_HAS_IMAGES</c> set to true and the two archive lists the
/// template reads; null leaves the image variables at the template's defaults (off).</param>
/// <param name="ExistingPipelines">R-70: the names of the pipelines the project already holds. A pipeline-name variable is also
/// written for a pipeline the wizard keeps (a second pass over a project that has its security and quality pipelines), with
/// its real name, so the candidate never falls back on the template's default name of a pipeline the project lacks.</param>
/// <param name="AskApprovalBeforeProduction">R-60: the deployment pipeline asks for an approval before it deploys, even for a release
/// graded A (<c>APPLICATION_DEPLOY_REQUIRE_APPROVAL</c>), which is the wizard's default; false writes it off.</param>
/// <param name="QaE2eCommands">R-59: the value of <c>APPLICATION_QA_E2E_COMMANDS</c> the QA pipeline is written with, from the browser tests
/// the repository holds (<see cref="PipelineSetupBrowserTests"/>); null writes none.</param>
/// <param name="QaExtendedCommands">R-67: the value of <c>APPLICATION_QA_EXTENDED_COMMANDS</c> the extended QA pipeline is written with;
/// null writes none.</param>
/// <param name="ExternalQualityMinimal">R-12: the quality of an external repository is the minimal one, which declares no command to run
/// (<c>APPLICATION_QUALITY_COMMANDS</c> empty): only the platform's complexity and lint of the languages found are published.</param>
/// <param name="QaE2eImage">R-20: the value of <c>APPLICATION_QA_E2E_IMAGE</c>, the browser image the QA pipeline runs
/// <paramref name="QaE2eCommands"/> in (<see cref="PipelineSetupBrowserTests.Image"/>), so the agent installs no browser library;
/// null writes none, and the commands then run on the agent.</param>
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
    bool RunDeployAfterCandidate = true,
    bool External = false,
    IReadOnlyList<string>? ImageArchives = null,
    IReadOnlySet<string>? ExistingPipelines = null,
    bool AskApprovalBeforeProduction = true,
    string? QaE2eCommands = null,
    string? QaExtendedCommands = null,
    string VersionPrefix = PipelineSetupYamlInput.DefaultVersionPrefix,
    bool ExternalQualityMinimal = false,
    string? QaE2eImage = null)
{
    /// <summary>R-33: the first series of a project the wizard sets up, so its first release is 0.1.0.</summary>
    internal const string DefaultVersionPrefix = "0.1";
}

/// <summary>
/// Writes the YAML of a pipeline the setup wizard creates: <c>extends: template@version</c> and the
/// variables the template reads. The point of this class is R2-11: an <c>APPLICATION_*_PIPELINE</c> variable
/// is written only for a pipeline that exists in the project, created by the wizard in this pass or already held
/// (R-70). A name written for a pipeline that does not exist would send a candidate to trigger it and fail at the
/// first run.
/// The wizard asks for no command (R2-25): the <c>APPLICATION_*_COMMANDS</c> variables are the template's own,
/// so a pipeline created here carries none and the template says so in its run when it has nothing to run.
/// </summary>
internal static class PipelineSetupYamlBuilder
{
    /// <summary>R-33: the variable naming the release series of the candidate and the fast release.</summary>
    internal const string VersionPrefixVariable = "APPLICATION_VERSION_PREFIX";

    /// <param name="production">What the production setup produced, for the production pipeline only (ADR-057): the
    /// Compose file, the strategy and the server. Null writes none of it.</param>
    /// <param name="templateVersion">The numbered version the pipeline is pinned to: the latest published one at
    /// creation (user decision 2026-10-24). The pin stays explicit; "Update all pipelines" on the project catalogue
    /// or the fleet moves it to a newer version of the shipped generic template in one click (R-8).
    /// A generic template is never referenced as "latest" or without a number, so a
    /// version that is not a real number (0 from a catalog that did not carry it) is refused instead of written.</param>
    public static string Build(string pipelineName, string templateName, int templateVersion,
        PipelineSetupYamlInput input, PipelineSetupProductionInput? production = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(templateVersion, 1);
        var yaml = new StringBuilder();
        yaml.Append("name: ").Append(pipelineName).Append('\n');
        yaml.Append("extends: ").Append(templateName).Append('@').Append(templateVersion).Append('\n');
        yaml.Append(Trigger(templateName));
        if (!string.IsNullOrWhiteSpace(input.SourceBranch))
            yaml.Append("source_branch: \"").Append(Escape(input.SourceBranch)).Append("\"\n");
        var deployment = PipelineSetupRoles.IsDeployProdTemplate(templateName) || templateName == PipelineSetupRoles.ReleaseFastTemplate
            ? production : null;
        AppendDeployServer(yaml, deployment);
        AppendDeploymentChain(yaml, templateName, input);
        yaml.Append("variables:\n");
        foreach (var (key, value) in Variables(input).Concat(ImageVariables(templateName, input))
            .Concat(ProductionVariables(templateName, production)).Concat(DeclaredVariables(templateName, input)))
            AppendVariable(yaml, key, value);
        yaml.Append(DeliveryTemplateNames.Is(templateName, DeliveryTemplateNames.Quality) && input.Languages is { } languages
            ? languages.QualityStages(input.LintSourceStage, input.External)
            : ExternalSecurityStages(templateName, input));
        return yaml.ToString();
    }

    /// <summary>The nightly runs on its schedule; every other pipeline is launched by hand or by the chain. The scheduler
    /// reads the pipeline's own document only (<c>PipelineScheduleReader</c>), never the <c>schedule:</c> of the template
    /// it extends, so the nightly writes both the trigger and the cron of <c>application-nightly</c> (02:00 UTC).</summary>
    private static string Trigger(string templateName) =>
        DeliveryTemplateNames.Is(templateName, DeliveryTemplateNames.Nightly)
            ? "trigger: schedule\nschedule: \"" + NightlySchedule + "\"\n"
            : "trigger: manual\n";

    /// <summary>The cron of the <c>application-nightly</c> template, with seconds.</summary>
    internal const string NightlySchedule = "0 0 2 * * *";

    /// <summary>R-12: the complete security of an external repository is the security template without the stage that restores the
    /// CI manifest: no pipeline of ours builds an external repository, so there is no manifest to restore (the stages that waited for it
    /// wait for what it waited for). Every other pipeline keeps the template's stages as they are.</summary>
    private static string ExternalSecurityStages(string templateName, PipelineSetupYamlInput input) =>
        input.External && DeliveryTemplateNames.Is(templateName, DeliveryTemplateNames.Security)
            ? "stages:\n  - name: Restore CI manifest\n    remove: true"
            : "stages: []";

    /// <summary>R-72: a candidate that succeeds hands the release it published to the deployment pipeline the wizard
    /// creates in the same pass, as <c>aetheus-candidate</c> does by hand. Only the candidate template carries it, and only
    /// when that pipeline exists (a name written for a pipeline that does not exist would fail at the first success). The
    /// deployment pipeline asks for its approval by its own option (R-60, ticked by default), whatever the chain. The user decides it (user decision
    /// 2026-10-25): the wizard's "Run the deployment after the candidate" option, ticked by default, writes nothing when
    /// it is not.</summary>
    private static void AppendDeploymentChain(StringBuilder yaml, string templateName, PipelineSetupYamlInput input)
    {
        if (!input.RunDeployAfterCandidate
            || !DeliveryTemplateNames.Is(templateName, DeliveryTemplateNames.Candidate)
            || !(input.CreatedSuffixes.Contains(PipelineSetupRoles.DeployProd)
                || input.ExistingPipelines?.Contains($"{input.Prefix}-{PipelineSetupRoles.DeployProd}") == true)) return;
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

    /// <summary>The production pipeline gets the Compose file and the strategy (whether the API has its own host is the
    /// template it extends, R-11); the QA pipeline the Compose file only, for its disposable stack (R-19). R-37: the fast
    /// release deploys to the same host as the production pipeline, so it is given the same two (without them it looked
    /// for a Compose file at the root of the repository and, for a blue-green project, replaced the live services instead
    /// of cutting over).</summary>
    private static IEnumerable<(string Key, string Value)> ProductionVariables(
        string templateName, PipelineSetupProductionInput? production)
    {
        if (production is null) yield break;
        if (templateName == PipelineSetupRoles.QaTemplate)
        {
            if (!string.IsNullOrEmpty(production.ComposeFile)) yield return ("COMPOSE_FILE", production.ComposeFile);
            yield break;
        }
        if (!PipelineSetupRoles.IsDeployProdTemplate(templateName) && templateName != PipelineSetupRoles.ReleaseFastTemplate)
            yield break;
        // Only what is known: the Compose file and the strategy of a repository that was not read are not claimed.
        if (!string.IsNullOrEmpty(production.ComposeFile)) yield return ("COMPOSE_FILE", production.ComposeFile);
        if (!string.IsNullOrEmpty(production.Strategy)) yield return ("DEPLOY_STRATEGY", production.Strategy);
    }

    /// <summary>What the wizard declares for a pipeline on the user's behalf: the deployment's approval (R-60, always written, so
    /// the pipeline says its own choice), and the commands the QA pipelines run (R-59, R-67), written only when known.</summary>
    private static IEnumerable<(string Key, string Value)> DeclaredVariables(string templateName, PipelineSetupYamlInput input)
    {
        if (PipelineSetupRoles.IsDeployProdTemplate(templateName))
            yield return ("APPLICATION_DEPLOY_REQUIRE_APPROVAL", Flag(input.AskApprovalBeforeProduction));
        // R-33: the pipelines that publish a release say their version series, so the user raises it in the pipeline
        // (1.0 for the first major release) as Aetheus raises its VersionPrefix.
        if (DeliveryTemplateNames.Is(templateName, DeliveryTemplateNames.Candidate)
            || DeliveryTemplateNames.Is(templateName, DeliveryTemplateNames.ReleaseFast))
            yield return (VersionPrefixVariable, input.VersionPrefix);
        if (templateName == PipelineSetupRoles.QaTemplate && input.QaE2eCommands is { Length: > 0 } e2e)
        {
            yield return ("APPLICATION_QA_E2E_COMMANDS", e2e);
            if (input.QaE2eImage is { Length: > 0 } image) yield return ("APPLICATION_QA_E2E_IMAGE", image);
        }
        if (templateName == PipelineSetupRoles.QaExtendedTemplate && input.QaExtendedCommands is { Length: > 0 } extended)
            yield return ("APPLICATION_QA_EXTENDED_COMMANDS", extended);
    }

    /// <summary>R-5: the security pipeline of a project that ships container images. <c>APPLICATION_HAS_IMAGES</c> is the one
    /// variable that switches the image restore and the .NET runtime CVE check; the archives (<c>.tar.gz</c>) and their unpacked
    /// names (<c>.tar</c>) are what those steps read. A project that ships none gets none of them, so the template's own
    /// defaults (off) apply.</summary>
    private static IEnumerable<(string Key, string Value)> ImageVariables(string templateName, PipelineSetupYamlInput input)
    {
        if (!DeliveryTemplateNames.Is(templateName, DeliveryTemplateNames.Security)
            || input.ImageArchives is not { Count: > 0 } archives) yield break;
        yield return ("APPLICATION_HAS_IMAGES", "true");
        yield return ("APPLICATION_IMAGE_ARCHIVES", string.Join(' ', archives));
        yield return ("APPLICATION_IMAGE_TARS", string.Join(' ', archives.Select(PipelineSetupImages.UnpackedName)));
    }

    /// <summary>A variable of the pipeline: a double-quoted scalar, or, for a value of several lines (a .NET browser test command),
    /// a literal block scalar so the lines stay lines and the file reads as the command does.</summary>
    private static void AppendVariable(StringBuilder yaml, string key, string value)
    {
        if (!value.Contains('\n', StringComparison.Ordinal))
        {
            yaml.Append("  ").Append(key).Append(": \"").Append(Escape(value)).Append("\"\n");
            return;
        }

        yaml.Append("  ").Append(key).Append(": |\n");
        foreach (var line in value.Split('\n'))
            yaml.Append(line.Length == 0 ? string.Empty : "    ").Append(line).Append('\n');
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
        // The default commands run the .NET tests; without a .NET project they would fail, so an external repository of
        // another language declares none and the run says so instead.
        if (input.External && (input.ExternalQualityMinimal || input.Languages is { DotNet: false }))
            yield return ("APPLICATION_QUALITY_COMMANDS", string.Empty);
        yield return ("APPLICATION_SECURITY_ENABLED", Flag(input.Security));
        yield return ("APPLICATION_QA_ENABLED", Flag(input.Qa));
        yield return ("APPLICATION_QA_EXTENDED_ENABLED", Flag(input.QaExtended));
        yield return ("APPLICATION_SECURITY_HISTORY_ENABLED", Flag(input.SecurityHistory));
        foreach (var (variable, role) in PipelineVariables(input.AnalyticsPackageTemplate))
            if (input.CreatedSuffixes.Contains(role) || input.ExistingPipelines?.Contains($"{input.Prefix}-{role}") == true)
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
