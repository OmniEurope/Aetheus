// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Services;

namespace Aetheus.Back.Components.Pipelines;

public sealed partial class PipelineSetupReadinessService(
    IPipelineTemplateService templates,
    IPipelineRepository pipelines,
    IGitLightService git,
    IGitLightRepository gitRepositories,
    IPipelineTemplateResolver templateResolver,
    IPipelineRequirementsChecker requirements,
    ILogger<PipelineSetupReadinessService> logger) : IPipelineSetupReadinessService
{
    /// <summary>
    /// The generic templates delegate every project-specific act to an adapter under
    /// <c>.pipeline/scripts/</c>, guarded by a literal <c>test -f</c>. A missing adapter is therefore
    /// not a subtle misconfiguration but a guaranteed stage failure, and the path is right there in
    /// the template's shell, which is why it can be extracted rather than assumed.
    /// </summary>
    [GeneratedRegex(@"\.pipeline/scripts/[A-Za-z0-9._-]+\.sh", RegexOptions.CultureInvariant)]
    private static partial Regex AdapterScriptPattern { get; }

    public async Task<PipelineSetupReadinessDto> CheckAsync(
        int projectId,
        IReadOnlyList<string> templateNames,
        int? organizationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(templateNames);

        var definitions = await LoadSelectedDefinitionsAsync(templateNames, ct).ConfigureAwait(false);
        return await CheckDefinitionsAsync(definitions, projectId, organizationId, ct).ConfigureAwait(false);
    }

    public async Task<int?> FindPipelineProjectIdAsync(int pipelineId, CancellationToken ct = default) =>
        await pipelines.FindPipelineAsync(pipelineId, ct).ConfigureAwait(false) is { } pipeline
            ? await pipelines.GetPipelineProjectIdAsync(pipeline, ct).ConfigureAwait(false)
            : null;

    public async Task<PipelineSetupReadinessDto> CheckProjectAsync(
        int projectId,
        int? pipelineId,
        int? organizationId,
        CancellationToken ct = default)
    {
        var definitions = new List<PipelineYamlDefinition>();
        int? owningOrganizationId = null;
        foreach (var (id, _, yaml) in await pipelines.GetPipelineDefinitionsForProjectAsync(projectId, ct).ConfigureAwait(false))
        {
            if (pipelineId is { } only && id != only) continue;
            owningOrganizationId ??= await pipelines.GetPipelineOrganizationIdAsync(id, ct).ConfigureAwait(false);
            if (await ResolveDefinitionAsync(yaml, owningOrganizationId, ct).ConfigureAwait(false) is { } definition)
                definitions.Add(definition);
        }

        // No pipeline, nothing a run could need: an empty verdict rather than a "no repository" for a
        // project that has not asked for anything yet.
        if (definitions.Count == 0) return new PipelineSetupReadinessDto();
        return await CheckDefinitionsAsync(definitions, projectId, organizationId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The pipeline as a run would read it: its <c>extends:</c> template expanded through the same
    /// resolver the launch uses, so the adapters a template invokes count for the pipeline that extends
    /// it. A template that cannot be resolved falls back to the pipeline's own YAML.
    /// </summary>
    private async Task<PipelineYamlDefinition?> ResolveDefinitionAsync(string yaml, int? organizationId, CancellationToken ct)
    {
        if (organizationId is { } organization)
        {
            try
            {
                return (await templateResolver.ResolveAsync(yaml, organization, parameters: null, ct).ConfigureAwait(false)).Definition;
            }
            catch (BadRequestException exception)
            {
                logger.LogDebug(exception, "Project readiness could not resolve a pipeline template; reading the pipeline's own YAML.");
            }
        }

        return YamlParsingHelper.ParseAndValidate(yaml, logger);
    }

    private async Task<PipelineSetupReadinessDto> CheckDefinitionsAsync(
        List<PipelineYamlDefinition> definitions,
        int projectId,
        int? organizationId,
        CancellationToken ct)
    {
        var stages = definitions.SelectMany(YamlParsingHelper.FlattenJobs).ToList();
        var checks = new List<PipelineSetupReadinessCheckDto>();

        // The declaration the templates carry, read here and by the launch preflight: one statement
        // of what an installation must provide, rather than two inferences of the same answer.
        await CheckDeclaredRequirementsAsync(definitions, projectId, organizationId, checks, ct)
            .ConfigureAwait(false);
        await CheckRunnersAsync(stages, organizationId, checks, ct).ConfigureAwait(false);
        await CheckEnvironmentsAsync(stages, projectId, checks, ct).ConfigureAwait(false);
        var (inspected, branch) = await CheckAdapterScriptsAsync(stages, projectId, checks, ct).ConfigureAwait(false);

        return new PipelineSetupReadinessDto
        {
            Checks = checks,
            RepositoryInspected = inspected,
            InspectedBranch = branch
        };
    }

    /// <summary>
    /// Flattens every selected template into its stages. A template name the instance does not have
    /// is skipped rather than reported: the wizard cannot select one, and inventing a finding for it
    /// would put a message on screen that no user action can clear.
    /// </summary>
    private async Task<List<PipelineYamlDefinition>> LoadSelectedDefinitionsAsync(
        IReadOnlyList<string> templateNames,
        CancellationToken ct)
    {
        var summaries = await templates.GetTemplatesAsync(ct).ConfigureAwait(false);
        var definitions = new List<PipelineYamlDefinition>();
        foreach (var name in templateNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var summary = summaries.Find(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (summary is null) continue;

            var template = await templates.GetTemplateAsync(summary.Id, ct).ConfigureAwait(false);
            if (template is null) continue;

            var definition = YamlParsingHelper.ParseAndValidate(template.YamlContent, logger);
            if (definition is null)
            {
                // An unparseable stored template is a template problem, not a project readiness
                // problem, and the editor already surfaces it. Skipping keeps this check honest
                // about what it did NOT read rather than blaming the project.
                logger.LogWarning(
                    "Setup readiness skipped template '{Template}': its stored YAML does not parse.",
                    template.Name);
                continue;
            }

            definitions.Add(definition);
        }

        return definitions;
    }

    /// <summary>
    /// The <c>requires:</c> block of every selected template. Reported per class so the wizard can
    /// name what to create (a library, a vault) rather than saying only that something is missing.
    /// </summary>
    private async Task CheckDeclaredRequirementsAsync(
        List<PipelineYamlDefinition> definitions,
        int projectId,
        int? organizationId,
        List<PipelineSetupReadinessCheckDto> checks,
        CancellationToken ct)
    {
        var unsatisfied = new List<PipelineRequirementOutcome>();
        foreach (var definition in definitions)
            unsatisfied.AddRange((await requirements
                .CheckAsync(definition.Requires, projectId, organizationId, ct).ConfigureAwait(false))
                .Where(outcome => !outcome.Satisfied));
        if (unsatisfied.Count == 0) return;

        void Report(string kind, PipelineSetupReadinessKind reported)
        {
            var names = unsatisfied
                .Where(outcome => outcome.Kind == kind)
                .Select(outcome => outcome.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count == 0) return;
            checks.Add(new PipelineSetupReadinessCheckDto
            {
                Kind = reported,
                Severity = PipelineSetupReadinessSeverity.Blocking,
                Items = names
            });
        }

        Report(PipelineRequirementsChecker.LibraryKind, PipelineSetupReadinessKind.MissingRequiredLibraries);
        Report(PipelineRequirementsChecker.VaultKind, PipelineSetupReadinessKind.MissingRequiredVaults);
        // Environments already have their own check from the stage selectors; a declared one that is
        // missing joins the same finding rather than opening a second one saying the same thing.
        Report(PipelineRequirementsChecker.EnvironmentKind, PipelineSetupReadinessKind.MissingEnvironments);
        Report(PipelineRequirementsChecker.CapabilityKind, PipelineSetupReadinessKind.MissingRequiredCapabilities);
    }

    /// <summary>
    /// The generic templates carry no pool, environment or agent selector, so at dispatch they fall
    /// back to any runner. The only condition that cannot resolve itself is therefore "no runner is
    /// configured at all", which is what is reported here - never "the runner is offline", which the
    /// scheduler parks and retries rather than failing.
    /// </summary>
    private async Task CheckRunnersAsync(
        List<PipelineStageDefinition> stages,
        int? organizationId,
        List<PipelineSetupReadinessCheckDto> checks,
        CancellationToken ct)
    {
        if (stages.Count == 0) return;

        // No selector: the question is the organization's whole fleet, no environment to scope.
        var anyRunner = await pipelines.FindCandidateTargetServerIdsAsync(
            null, null, null, OsType.Unknown, organizationId, false, projectId: null, ct).ConfigureAwait(false);
        if (anyRunner.Count == 0)
        {
            checks.Add(new PipelineSetupReadinessCheckDto
            {
                Kind = PipelineSetupReadinessKind.NoRunnerConfigured,
                Severity = PipelineSetupReadinessSeverity.Blocking
            });
            // Without a single runner the deploy-specific question is already answered; asking it
            // again would show the user two rows for one missing server.
            return;
        }

        var deployStages = stages.Where(HasDeployWorkload).Select(stage => stage.Name).ToList();
        if (deployStages.Count == 0) return;

        var deployRunners = await pipelines.FindCandidateTargetServerIdsAsync(
            null, null, null, OsType.Unknown, organizationId, true, projectId: null, ct).ConfigureAwait(false);
        if (deployRunners.Count == 0)
            checks.Add(new PipelineSetupReadinessCheckDto
            {
                Kind = PipelineSetupReadinessKind.NoDeployRunnerConfigured,
                Severity = PipelineSetupReadinessSeverity.Blocking,
                Items = deployStages
            });
    }

    private static bool HasDeployWorkload(PipelineStageDefinition stage) =>
        string.Equals(stage.ExecutionRole, "deploy", StringComparison.OrdinalIgnoreCase)
        || stage.Steps.Exists(step => string.Equals(step.Type, "deploy", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// An environment a stage names must exist before the run reaches that stage. Names that are
    /// still a variable reference at template level are skipped: their value comes from the pipeline
    /// the wizard is about to write, so they cannot be resolved here and a finding would be a guess.
    /// </summary>
    private async Task CheckEnvironmentsAsync(
        List<PipelineStageDefinition> stages,
        int projectId,
        List<PipelineSetupReadinessCheckDto> checks,
        CancellationToken ct)
    {
        var names = stages
            .Select(stage => stage.Environment)
            .Where(name => !string.IsNullOrWhiteSpace(name)
                && !name!.Contains("$(", StringComparison.Ordinal)
                && !name.Contains("${{", StringComparison.Ordinal))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = new List<string>();
        foreach (var name in names)
        {
            var environment = await pipelines
                .FindEnvironmentByNameForProjectAsync(name, projectId, ct).ConfigureAwait(false);
            if (environment is null) missing.Add(name);
        }

        if (missing.Count > 0)
            checks.Add(new PipelineSetupReadinessCheckDto
            {
                Kind = PipelineSetupReadinessKind.MissingEnvironments,
                Severity = PipelineSetupReadinessSeverity.Blocking,
                Items = missing
            });
    }

    /// <summary>
    /// Reads the project's repository once and compares the adapter directory against the paths the
    /// selected templates invoke. Returns whether the repository could be read at all, so the caller
    /// can say "not checked" instead of letting an empty finding list read as "all present".
    /// </summary>
    private async Task<(bool Inspected, string? Branch)> CheckAdapterScriptsAsync(
        List<PipelineStageDefinition> stages,
        int projectId,
        List<PipelineSetupReadinessCheckDto> checks,
        CancellationToken ct)
    {
        var required = RequiredAdapterScripts(stages);

        // An external repository attached BESIDE the project's own is a source some pipelines name, not
        // where the project's adapters live; the project's own repository (internal, or the mirror of
        // its external source) is the one read.
        var repositories = (await git.GetRepositoriesAsync(projectId, ct).ConfigureAwait(false))
            .Where(item => !item.IsAdditionalSource)
            .ToList();
        var repository = repositories.Find(item => !item.IsEmpty) ?? repositories.FirstOrDefault();
        var source = await gitRepositories.GetProjectRepositorySourceAsync(projectId, ct).ConfigureAwait(false);
        if (repository is null)
        {
            // Recette R1-32: a project whose source is an external address has a repository, Aetheus
            // just cannot read it. Saying "no repository" there was false; saying the files are missing
            // would be too. What is true: they could not be checked, and here is what to check.
            if (source is { HasSource: true })
            {
                ReportExternalNotInspected(stages, checks);
                return (false, null);
            }

            checks.Add(new PipelineSetupReadinessCheckDto
            {
                Kind = PipelineSetupReadinessKind.NoRepository,
                Severity = PipelineSetupReadinessSeverity.Blocking
            });
            return (false, null);
        }

        var branch = string.IsNullOrWhiteSpace(repository.DefaultBranch) ? "main" : repository.DefaultBranch;
        if (repository.IsEmpty && source?.GitConnectionId is not null)
        {
            // The mirror of an external source that holds no commit yet has not been synchronized: the
            // external repository may well carry the files, Aetheus has not fetched them.
            ReportExternalNotInspected(stages, checks);
            return (false, branch);
        }

        if (repository.IsEmpty)
        {
            checks.Add(new PipelineSetupReadinessCheckDto
            {
                Kind = PipelineSetupReadinessKind.EmptyBranch,
                Severity = PipelineSetupReadinessSeverity.Blocking,
                Items = [branch]
            });
            return (false, branch);
        }

        AddMissing(PipelineSetupReadinessKind.MissingAdapterScripts,
            await FindMissingFilesAsync(repository.Id, branch, required, ct).ConfigureAwait(false), checks);
        // Recette R2-121: a platform scanner that reads its configuration from the repository fails
        // without it, exactly like a missing adapter.
        AddMissing(PipelineSetupReadinessKind.MissingScannerConfiguration,
            await FindMissingFilesAsync(repository.Id, branch, RequiredScannerFiles(stages), ct).ConfigureAwait(false),
            checks);

        return (true, branch);
    }

    private static List<string> RequiredAdapterScripts(IEnumerable<PipelineStageDefinition> stages) =>
        stages
            .SelectMany(stage => stage.Steps)
            .SelectMany(step => AdapterScriptPattern.Matches(step.Shell).Select(match => match.Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A warning naming the repository files the selected pipelines need, when the repository is
    /// external and unreadable from here. Nothing is reported when the pipelines need no such file.
    /// </summary>
    private static void ReportExternalNotInspected(
        List<PipelineStageDefinition> stages, List<PipelineSetupReadinessCheckDto> checks)
    {
        var needed = RequiredAdapterScripts(stages).Concat(RequiredScannerFiles(stages)).ToList();
        if (needed.Count == 0) return;
        checks.Add(new PipelineSetupReadinessCheckDto
        {
            Kind = PipelineSetupReadinessKind.ExternalRepositoryNotInspected,
            Severity = PipelineSetupReadinessSeverity.Warning,
            Items = needed
        });
    }

    private static void AddMissing(
        PipelineSetupReadinessKind kind, List<string> missing, List<PipelineSetupReadinessCheckDto> checks)
    {
        if (missing.Count > 0)
            checks.Add(new PipelineSetupReadinessCheckDto
            {
                Kind = kind,
                Severity = PipelineSetupReadinessSeverity.Blocking,
                Items = missing
            });
    }

    /// <summary>
    /// The repository files the selected scanner steps read, taken from the scanner manifest itself:
    /// the value of a <c>--config</c> argument inside the scanned source (<c>/src/.gitleaks.toml</c>).
    /// A path the scanner merely accepts when present (an ignore file) is not a <c>--config</c>.
    /// </summary>
    internal static List<string> RequiredScannerFiles(IEnumerable<PipelineStageDefinition> stages) =>
        stages
            .SelectMany(stage => stage.Steps)
            .Where(step => string.Equals(step.Type, "scanner", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(step.Scanner))
            .Select(step => ScannerManifestCatalog.Find(step.Scanner!))
            .OfType<ScannerManifestEntry>()
            .SelectMany(scanner => scanner.Arguments
                .Zip(scanner.Arguments.Skip(1))
                .Where(pair => pair.First == "--config" && pair.Second.StartsWith(ScannedSourceRoot, StringComparison.Ordinal))
                .Select(pair => pair.Second[ScannedSourceRoot.Length..]))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    /// <summary>Where a scanner container sees the repository.</summary>
    private const string ScannedSourceRoot = "/src/";

    /// <summary>
    /// The paths among <paramref name="paths"/> absent from the branch, each directory listed once.
    /// A directory that cannot be listed counts as holding nothing, never as a silent pass: the run
    /// would fail on the same missing file.
    /// </summary>
    private async Task<List<string>> FindMissingFilesAsync(
        int repositoryId, string branch, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        var missing = new List<string>();
        foreach (var directory in paths.GroupBy(path => path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty))
        {
            var present = await ListFileNamesAsync(repositoryId, branch, directory.Key, ct).ConfigureAwait(false);
            missing.AddRange(directory.Where(path => !present.Contains(path[(path.LastIndexOf('/') + 1)..])));
        }
        return missing;
    }

    private async Task<HashSet<string>> ListFileNamesAsync(
        int repositoryId, string branch, string directory, CancellationToken ct)
    {
        List<GitLightTreeEntryDto> entries;
        try
        {
            entries = await git.GetTreeAsync(repositoryId, branch, directory.Length == 0 ? null : directory, ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                exception,
                "Setup readiness could not list '{Directory}' on '{Branch}' of repository {RepositoryId}.",
                directory, branch, repositoryId);
            entries = [];
        }
        return entries
            .Where(entry => entry.Type == GitTreeEntryType.Blob)
            .Select(entry => entry.Name)
            .ToHashSet(StringComparer.Ordinal);
    }
}
