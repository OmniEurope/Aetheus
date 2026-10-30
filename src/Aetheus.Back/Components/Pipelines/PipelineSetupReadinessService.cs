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
    IProjectConfigRepositoryService configRepository,
    ILogger<PipelineSetupReadinessService> logger) : IPipelineSetupReadinessService
{
    private readonly PipelineEnvTemplateNames _envTemplateNames = new(configRepository, git, logger);

    /// <summary>
    /// The generic templates require no file of the project (ADR-050): its commands are declared in its
    /// pipeline. A pipeline that still calls a script under <c>.pipeline/scripts/</c> in its own shell
    /// fails on a missing one, and the path is right there in the shell, which is why it can be
    /// extracted rather than assumed.
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

        var definitions = await LoadSelectedDefinitionsAsync(templateNames, projectId, ct).ConfigureAwait(false);
        // The wizard writes no command: the templates' defaults are what the new pipelines will run.
        // Nothing exists yet to link to: a template is named by its own definition.
        var owners = definitions.Select(definition => ((int?)null, definition.Name)).ToList();
        return await CheckDefinitionsAsync(definitions, owners, new HashSet<string>(), projectId, organizationId, ct).ConfigureAwait(false);
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
        // The pipeline each definition comes from, in the same order, for the tables of empty values.
        var owners = new List<(int? Id, string Name)>();
        // The commands a pipeline declares itself in its own YAML: their template default does not run.
        var ownCommands = new HashSet<string>(StringComparer.Ordinal);
        int? owningOrganizationId = null;
        foreach (var (id, name, yaml) in await pipelines.GetPipelineDefinitionsForProjectAsync(projectId, ct).ConfigureAwait(false))
        {
            if (pipelineId is { } only && id != only) continue;
            owningOrganizationId ??= await pipelines.GetPipelineOrganizationIdAsync(id, ct).ConfigureAwait(false);
            if (await ResolveDefinitionAsync(yaml, owningOrganizationId, ct).ConfigureAwait(false) is { } definition)
            {
                definitions.Add(definition);
                owners.Add((id, name));
            }
            if (YamlParsingHelper.ParseAndValidate(yaml, logger) is { } own)
                ownCommands.UnionWith(own.Variables.Keys);
        }

        // No pipeline, nothing a run could need: an empty verdict rather than a "no repository" for a
        // project that has not asked for anything yet.
        if (definitions.Count == 0) return new PipelineSetupReadinessDto();
        return await CheckDefinitionsAsync(definitions, owners, ownCommands, projectId, organizationId, ct).ConfigureAwait(false);
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
        IReadOnlyList<(int? Id, string Name)> owners,
        IReadOnlySet<string> ownCommands,
        int projectId,
        int? organizationId,
        CancellationToken ct)
    {
        // R3-67: the names the project's runs will read, `${{ project.slug }}.share` already this
        // project's, so what the wizard reports missing is what it creates.
        var slug = await PipelineProjectResourceNames.FindSlugAsync(pipelines, projectId, ct).ConfigureAwait(false);
        definitions = [.. definitions.Select(definition => PipelineProjectResourceNames.Expand(definition, slug))];
        var stages = definitions.SelectMany(YamlParsingHelper.FlattenJobs).ToList();
        var checks = new List<PipelineSetupReadinessCheckDto>();

        // The declaration the templates carry, read here and by the launch preflight: one statement
        // of what an installation must provide, rather than two inferences of the same answer.
        await CheckDeclaredRequirementsAsync(definitions, projectId, organizationId, checks, ct)
            .ConfigureAwait(false);
        await CheckEmptyValuesAsync(definitions, owners, projectId, checks, ct).ConfigureAwait(false);
        await CheckRunnersAsync(stages, organizationId, checks, ct).ConfigureAwait(false);
        await CheckEnvironmentsAsync(stages, projectId, checks, ct).ConfigureAwait(false);
        var (inspected, branch, repositoryId) = await CheckAdapterScriptsAsync(stages, projectId, checks, ct).ConfigureAwait(false);

        // What the templates' default commands need, read from the branch files and the fleet.
        var detection = await new PipelineSetupDetection(pipelines, git, logger)
            .DetectAsync(PipelineSetupDetection.DefaultCommands(definitions, ownCommands),
                inspected ? repositoryId : null, branch, organizationId, checks, ct)
            .ConfigureAwait(false);

        return new PipelineSetupReadinessDto
        {
            Checks = checks,
            RepositoryInspected = inspected,
            InspectedBranch = branch,
            Detection = detection
        };
    }

    /// <summary>
    /// Flattens every selected template into its stages. A template name the instance does not have
    /// is skipped rather than reported: the wizard cannot select one, and inventing a finding for it
    /// would put a message on screen that no user action can clear. A name resolves as the template
    /// resolver will run it (R-78): the project's organization's own template, otherwise the delivery
    /// template the platform shares; never another organization's.
    /// </summary>
    private async Task<List<PipelineYamlDefinition>> LoadSelectedDefinitionsAsync(
        IReadOnlyList<string> templateNames,
        int projectId,
        CancellationToken ct)
    {
        var projectOrganizationId = await pipelines.GetPipelineOwnerOrganizationIdAsync(
            projectId, null, null, ct).ConfigureAwait(false);
        var definitions = new List<PipelineYamlDefinition>();
        foreach (var name in templateNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = (projectOrganizationId is { } organizationId
                    ? await pipelines.FindTemplateByNameAsync(name, organizationId, ct).ConfigureAwait(false)
                    : null)
                ?? await pipelines.FindSharedTemplateByNameAsync(name, ct).ConfigureAwait(false);
            if (found is null) continue;

            var template = await templates.GetTemplateAsync(found.Id, ct).ConfigureAwait(false);
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
                .CheckAsync(DeclaredAndReadResources(definition), projectId, organizationId, ct).ConfigureAwait(false))
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
    /// The values a run would read EMPTY: a copied library or vault is created with every value empty,
    /// and an empty string is a resolved value, so no other check notices it. A vault secret is never
    /// legitimately empty, so every empty one of a vault the pipelines read is reported; a library
    /// entry may be empty on purpose, so it is reported only when a pipeline uses it as
    /// <c>$(NAME)</c> without a default. These are the same rules the launch applies
    /// (<see cref="PipelineUnresolvedVariableGuard.ValidateNoEmptyValues"/>).
    /// </summary>
    private async Task CheckEmptyValuesAsync(
        List<PipelineYamlDefinition> definitions,
        IReadOnlyList<(int? Id, string Name)> owners,
        int projectId,
        List<PipelineSetupReadinessCheckDto> checks,
        CancellationToken ct)
    {
        // R-71/R-74: each pipeline is judged on ITS libraries and vaults only, as its launch resolves
        // them. Merging every pipeline's libraries into one resolution let a key left empty in a
        // library one pipeline reads (the QA library, for instance) be reported against pipelines
        // that never read that library, and a later library overrode an earlier one's value.
        // R-10: one row per empty value and pipeline that reads it, which is what the finding shows.
        var empty = new List<PipelineEmptyValue>();
        var libraryRows = new List<PipelineSetupReadinessEmptyValueDto>();
        var vaultRows = new List<PipelineSetupReadinessEmptyValueDto>();
        var resolvedByReads = new Dictionary<string, IReadOnlyList<PipelineEmptyValue>>(StringComparer.Ordinal);
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            var (pipelineId, pipelineName) = owners[index];
            var libraryNames = definition.VariableLibraries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var vaultNames = definition.Vaults.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (libraryNames.Count == 0 && vaultNames.Count == 0) continue;

            var readKey = string.Join(',', libraryNames.Order(StringComparer.OrdinalIgnoreCase))
                          + "|" + string.Join(',', vaultNames.Order(StringComparer.OrdinalIgnoreCase));
            if (!resolvedByReads.TryGetValue(readKey, out var emptyOfDefinition))
                resolvedByReads[readKey] = emptyOfDefinition =
                    await requirements.FindEmptyValuesAsync(libraryNames, vaultNames, projectId, ct).ConfigureAwait(false);

            // R-7: only a key the pipeline reads as $(NAME) with no default is reported; $(NAME:-) says an
            // empty value is accepted, so that entry is optional for this pipeline and never "to fill".
            var emptyLibraryKeys = emptyOfDefinition.Where(value => value.Kind == PipelineRequirementsChecker.LibraryKind)
                .Select(value => value.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // R-68: the environment sample reads its variables as #{NAME}#; an empty entry it asks for is as much a
            // value to fill as one read as $(NAME), the run refusing it by name.
            var consumedKeys = PipelineUnresolvedVariableGuard.FindConsumedEmptyLibraryKeys(definition, emptyLibraryKeys).Keys.ToList();
            if (emptyLibraryKeys.Count > 0)
                consumedKeys.AddRange((await _envTemplateNames.ReadAsync(definition, projectId, ct).ConfigureAwait(false))
                    .Where(name => emptyLibraryKeys.Contains(name) && !consumedKeys.Contains(name, StringComparer.OrdinalIgnoreCase))
                    .ToList());
            foreach (var key in consumedKeys)
            {
                var value = emptyOfDefinition.First(item => item.Kind == PipelineRequirementsChecker.LibraryKind
                    && string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
                empty.Add(value);
                libraryRows.Add(Row(value, pipelineId, pipelineName));
            }
            foreach (var secret in emptyOfDefinition.Where(value => value.Kind == PipelineRequirementsChecker.VaultKind))
            {
                empty.Add(secret);
                vaultRows.Add(Row(secret, pipelineId, pipelineName));
            }
        }

        var consumed = DistinctKeys(libraryRows);
        var secrets = DistinctKeys(vaultRows);

        if (consumed.Count > 0)
            checks.Add(new PipelineSetupReadinessCheckDto
            {
                Kind = PipelineSetupReadinessKind.EmptyLibraryValues,
                Severity = PipelineSetupReadinessSeverity.Blocking,
                Items = consumed,
                ItemIds = OwnerIds(empty, PipelineRequirementsChecker.LibraryKind, consumed),
                EmptyValues = Ordered(libraryRows)
            });
        if (secrets.Count > 0)
            checks.Add(new PipelineSetupReadinessCheckDto
            {
                Kind = PipelineSetupReadinessKind.EmptyVaultSecrets,
                Severity = PipelineSetupReadinessSeverity.Blocking,
                Items = secrets,
                ItemIds = OwnerIds(empty, PipelineRequirementsChecker.VaultKind, secrets),
                EmptyValues = Ordered(vaultRows)
            });
    }

    private static PipelineSetupReadinessEmptyValueDto Row(PipelineEmptyValue value, int? pipelineId, string pipelineName) => new()
    {
        OwnerId = value.OwnerId,
        OwnerName = value.OwnerName ?? string.Empty,
        Key = value.Key,
        PipelineId = pipelineId,
        PipelineName = pipelineName
    };

    private static List<string> DistinctKeys(IEnumerable<PipelineSetupReadinessEmptyValueDto> rows) =>
        [.. rows.Select(row => row.Key).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    private static List<PipelineSetupReadinessEmptyValueDto> Ordered(IEnumerable<PipelineSetupReadinessEmptyValueDto> rows) =>
        [.. rows
            .OrderBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.PipelineName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The library or vault holding each reported empty item (R2-27), so each is a link to its own page.</summary>
    private static Dictionary<string, int> OwnerIds(
        IReadOnlyList<PipelineEmptyValue> empty, string kind, IReadOnlyList<string> items)
    {
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            if (empty.FirstOrDefault(value => value.Kind == kind && value.OwnerId is not null
                    && string.Equals(value.Key, item, StringComparison.OrdinalIgnoreCase))?.OwnerId is { } id)
                ids[item] = id;
        return ids;
    }

    /// <summary>
    /// Recette R3-67: the <c>requires:</c> block plus the libraries and vaults the template reads
    /// (<c>variable_libraries:</c>, <c>vaults:</c>), the same list the project overview warns about, so
    /// the wizard creates what a run of the template loads.
    /// </summary>
    private static PipelineRequiresDefinition DeclaredAndReadResources(PipelineYamlDefinition definition)
    {
        var declared = definition.Requires ?? new PipelineRequiresDefinition();
        var referenced = PipelineRequirementsProvisioner.ReferencedResources(definition);
        return declared with
        {
            Libraries = referenced?.Libraries ?? [],
            Vaults = referenced?.Vaults ?? []
        };
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
    private async Task<(bool Inspected, string? Branch, int? RepositoryId)> CheckAdapterScriptsAsync(
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
            .Where(item => !item.IsAdditionalSource && !item.IsConfiguration)
            .ToList();
        var repository = repositories.Find(item => !item.IsEmpty) ?? repositories.FirstOrDefault();
        var source = await gitRepositories.GetProjectRepositorySourceAsync(projectId, ct).ConfigureAwait(false);
        if (repository is null)
        {
            // External sources may exist without being readable by Aetheus; report unverified
            // requirements rather than claiming the repository or files are missing.
            if (source is { HasSource: true })
            {
                ReportExternalNotInspected(stages, checks);
                return (false, null, null);
            }

            checks.Add(new PipelineSetupReadinessCheckDto
            {
                Kind = PipelineSetupReadinessKind.NoRepository,
                Severity = PipelineSetupReadinessSeverity.Blocking
            });
            return (false, null, null);
        }

        var branch = string.IsNullOrWhiteSpace(repository.DefaultBranch) ? "main" : repository.DefaultBranch;
        if (repository.IsEmpty && source?.GitConnectionId is not null)
        {
            // The mirror of an external source that holds no commit yet has not been synchronized: the
            // external repository may well carry the files, Aetheus has not fetched them.
            ReportExternalNotInspected(stages, checks);
            return (false, branch, null);
        }

        if (repository.IsEmpty)
        {
            checks.Add(new PipelineSetupReadinessCheckDto
            {
                Kind = PipelineSetupReadinessKind.EmptyBranch,
                Severity = PipelineSetupReadinessSeverity.Blocking,
                Items = [branch]
            });
            return (false, branch, null);
        }

        AddMissing(PipelineSetupReadinessKind.MissingAdapterScripts,
            await FindMissingFilesAsync(repository.Id, branch, required, ct).ConfigureAwait(false), checks);
        // A platform scanner that reads its configuration from the repository fails without it,
        // exactly like a missing adapter.
        AddMissing(PipelineSetupReadinessKind.MissingScannerConfiguration,
            await FindMissingFilesAsync(repository.Id, branch, RequiredScannerFiles(stages), ct).ConfigureAwait(false),
            checks);

        return (true, branch, repository.Id);
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
    /// A path the scanner merely accepts when present (an ignore file) is not a <c>--config</c>, nor is
    /// the configuration of a scanner the agent runs with its built-in rules when the file is absent
    /// (<see cref="OptionalConfigurationScanners"/>).
    /// </summary>
    internal static List<string> RequiredScannerFiles(IEnumerable<PipelineStageDefinition> stages) =>
        stages
            .SelectMany(stage => stage.Steps)
            .Where(step => string.Equals(step.Type, "scanner", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(step.Scanner))
            .Select(step => ScannerManifestCatalog.Find(step.Scanner!))
            .OfType<ScannerManifestEntry>()
            .Where(scanner => !OptionalConfigurationScanners.Contains(scanner.Key))
            .SelectMany(scanner => scanner.Arguments
                .Zip(scanner.Arguments.Skip(1))
                .Where(pair => pair.First == "--config" && pair.Second.StartsWith(ScannedSourceRoot, StringComparison.Ordinal))
                .Select(pair => pair.Second[ScannedSourceRoot.Length..]))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    /// <summary>Recette R3-63, option 3: Gitleaks reads <c>.gitleaks.toml</c> when the repository has
    /// one and its built-in rules otherwise (the agent drops the <c>--config</c> of an absent file,
    /// <c>ScannerOptionalRepositoryFile</c>), so the file is never required.</summary>
    private static readonly HashSet<string> OptionalConfigurationScanners =
        new(["gitleaks", "gitleaks-history"], StringComparer.OrdinalIgnoreCase);

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
