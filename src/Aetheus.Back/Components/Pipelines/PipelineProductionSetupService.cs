// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.PortAllocation;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Shared.Components.Vaults;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The production setup of a project (ADR-057): what its Compose file asks for, and the creation of
/// every variable that file reads, so the generic production pipeline runs without a file on the server.
/// </summary>
public interface IPipelineProductionSetupService
{
    /// <summary>Reads the project's repository: the Compose files, the one proposed (or the one asked for), the
    /// variables it reads and where each value comes from. Writes nothing.</summary>
    Task<PipelineProductionAnalysisDto> AnalyzeAsync(int projectId, string? composeFile, CancellationToken ct = default);

    /// <summary><see cref="AnalyzeAsync(int, string?, CancellationToken)"/>, offering the servers of
    /// <paramref name="servers"/> when the project has none (recette R-77).</summary>
    Task<PipelineProductionAnalysisDto> AnalyzeAsync(
        int projectId, string? composeFile, ProductionServerScope servers, CancellationToken ct = default);

    /// <summary>Creates what the analysis proposed: the variables in the libraries and the vault <c>prod</c> of the
    /// project, the ports in the port registry, and the project's configuration repository with its versioned
    /// files. An existing entry is never overwritten.</summary>
    Task<PipelineProductionApplyResultDto> ApplyAsync(PipelineProductionApplyRequest request, CancellationToken ct = default);

    /// <summary><see cref="ApplyAsync(PipelineProductionApplyRequest, CancellationToken)"/>, where the server chosen may be
    /// one of <paramref name="servers"/> offered to a project that has none: it is then attached to the project first
    /// (recette R-77), so its ports are reserved and the deployment runs on it.</summary>
    Task<PipelineProductionApplyResultDto> ApplyAsync(
        PipelineProductionApplyRequest request, ProductionServerScope servers, CancellationToken ct = default);

    /// <summary>Mends the environment sample the project's configuration repository already holds (recette R-5): a token
    /// that names a variable the agent refuses to hand a step (<c>#{ENV}#</c>) becomes its scoped name
    /// (<c>#{DEPLOY_ENV}#</c>), and the library entry <c>prod</c> carries its value under that name, none lost. Touches
    /// nothing outside the project's own configuration repository and library; a sample with no such token is left as
    /// it is, so mending twice changes nothing. Audited.</summary>
    Task<PipelineProductionRepairResultDto> RepairEnvSampleAsync(int projectId, CancellationToken ct = default);
}

/// <summary>The servers the caller administers (a launch needs Admin on its target servers), offered to a project that has
/// none (recette R-77). Null ids: every server (the caller administers them all); <see cref="None"/> offers nothing.</summary>
public sealed record ProductionServerScope(IReadOnlyCollection<int>? AdministeredServerIds)
{
    public static ProductionServerScope None { get; } = new([]);
}

public sealed class PipelineProductionSetupService(
    IPipelineRepository pipelines,
    IGitLightService git,
    IVariableLibraryService libraries,
    IVaultService vaults,
    IPortAllocationService ports,
    IProjectConfigRepositoryService configRepository,
    IAuditService audit,
    IPipelineSetupServerRepository setupServers,
    ILogger<PipelineProductionSetupService> logger) : IPipelineProductionSetupService
{
    private const string ProdSuffix = ".prod";

    /// <summary>The library entry that names a blue-green project's Apache <c>Define</c> prefix.</summary>
    internal const string UpstreamDefineKey = "UPSTREAM_DEFINE";
    internal const string UpstreamConfKey = "UPSTREAM_CONF";
    internal const string UpstreamLinkKey = "UPSTREAM_LINK";
    internal const string ReloadHelperKey = "APACHE_RELOAD_HELPER";

    // Where Apache reads its sites on the Debian family the agent installer supports; the first file loaded defines the
    // ports the virtual hosts read, hence the 000 prefix.
    private const string SitesAvailable = "/etc/apache2/sites-available";
    private const string SitesEnabled = "/etc/apache2/sites-enabled";

    // The root-owned helper the agent installer publishes to reload Apache after a configuration check.
    private const string ReloadHelper = "/usr/local/lib/aetheus/aetheus-apache-reload";

    private const int ResourceSearchPageSize = 50;

    /// <summary>What applying the setup will write, collected before anything is written.</summary>
    private sealed class Writes
    {
        public List<ProductionAppliedEntryDto> Entries { get; } = [];
        public List<CreateVariableEntryRequest> LibraryAdds { get; } = [];
        public List<(int EntryId, string Key, string Value)> LibraryUpdates { get; } = [];
        public List<CreateVaultSecretRequest> VaultAdds { get; } = [];
        public List<string> PortKeys { get; } = [];
    }

    /// <summary>What one reading of the project found, shared by the analysis and the setup that follows it.</summary>
    private sealed record Inspection(
        PipelineProductionAnalysisDto Analysis,
        IReadOnlyList<ProductionVariablePlan> Plans,
        string Slug,
        VariableLibraryDto? Library,
        VaultDto? Vault,
        Dictionary<string, VariableEntryDto> LibraryEntries,
        HashSet<string> VaultKeys);

    public Task<PipelineProductionAnalysisDto> AnalyzeAsync(
        int projectId, string? composeFile, CancellationToken ct = default) =>
        AnalyzeAsync(projectId, composeFile, ProductionServerScope.None, ct);

    public async Task<PipelineProductionAnalysisDto> AnalyzeAsync(
        int projectId, string? composeFile, ProductionServerScope servers, CancellationToken ct = default) =>
        (await InspectAsync(projectId, composeFile, servers, ct).ConfigureAwait(false)).Analysis;

    public Task<PipelineProductionApplyResultDto> ApplyAsync(
        PipelineProductionApplyRequest request, CancellationToken ct = default) =>
        ApplyAsync(request, ProductionServerScope.None, ct);

    public async Task<PipelineProductionApplyResultDto> ApplyAsync(
        PipelineProductionApplyRequest request, ProductionServerScope servers, CancellationToken ct = default)
    {
        ValidateRequest(request);

        var inspection = await InspectAsync(request.ProjectId, request.ComposeFile, servers, ct).ConfigureAwait(false);
        var analysis = inspection.Analysis;
        if (!analysis.Readable || !string.Equals(analysis.ComposeFile, request.ComposeFile, StringComparison.Ordinal))
            throw new BadRequestException(
                $"The Compose file '{request.ComposeFile}' was not found in the project's repository.");
        var library = inspection.Library
            ?? throw new BadRequestException(
                $"The variable library '{inspection.Slug}{ProdSuffix}' does not exist: create the libraries and vaults first.");
        var vault = inspection.Vault
            ?? throw new BadRequestException(
                $"The vault '{inspection.Slug}{ProdSuffix}' does not exist: create the libraries and vaults first.");
        var server = string.IsNullOrWhiteSpace(request.Server)
            ? null
            : analysis.Servers.Find(item => string.Equals(item.Name, request.Server, StringComparison.Ordinal))
                ?? throw new BadRequestException($"'{request.Server}' is not a server of this project.");

        // Recette R-89: the strategy the operator chose rules the ports, the upstream entries and the virtual hosts.
        var strategy = string.IsNullOrWhiteSpace(request.Strategy) ? analysis.Strategy : request.Strategy;
        var plans = ProductionVariableClassifier.ForStrategy(inspection.Plans, strategy);
        var declaresApi = ProductionVariableClassifier.DeclaresApi(plans, strategy);

        var writes = new Writes();
        foreach (var plan in plans)
            Plan(plan, inspection, request.Values, writes);
        if (strategy == ProductionStrategy.BlueGreen)
            PlanUpstream(inspection, writes);

        // Port allocation names the library's project as holder and only needs the server to exist, so the
        // candidate is attached last: a failed write or repository creation never leaves it attached unconfigured.
        await WriteAsync(writes, library.Id, vault.Id, server?.Id, inspection.LibraryEntries, ct).ConfigureAwait(false);
        var entries = writes.Entries;

        var repository = await configRepository.EnsureAsync(
            request.ProjectId, ConfigFiles(plans, declaresApi, strategy == ProductionStrategy.BlueGreen), ct).ConfigureAwait(false);
        if (server is { Candidate: true })
            await AttachAsync(request.ProjectId, server, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Production setup of project {ProjectId}: {Created} created, {Kept} kept, configuration repository {Repository}.",
            request.ProjectId, entries.Count(entry => entry.Outcome != ProductionEntryOutcome.Kept),
            entries.Count(entry => entry.Outcome == ProductionEntryOutcome.Kept), repository.Slug);

        return new PipelineProductionApplyResultDto
        {
            ComposeFile = request.ComposeFile,
            Server = server?.Name ?? string.Empty,
            Strategy = strategy,
            DeclaresApi = declaresApi,
            Entries = entries,
            ConfigRepository = repository
        };
    }

    /// <summary>Recette R-77: the server chosen for a project that had none becomes the project's, as the project's
    /// Servers tab would have done, so the deployment and its run dialog find it. Called once the ports, variables,
    /// secrets and configuration repository are written.</summary>
    private async Task AttachAsync(int projectId, ProductionServerDto server, CancellationToken ct)
    {
        if (!await setupServers.AttachAgentServerAsync(projectId, server.Id, ct).ConfigureAwait(false)) return;
        await audit.LogAsync("ProductionServerAttached", "Project", projectId, server.Name, ct).ConfigureAwait(false);
        logger.LogInformation("Production setup of project {ProjectId}: server {Server} attached.", projectId, server.Name);
    }

    private static void ValidateRequest(PipelineProductionApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ProductionComposeFiles.IsSafePath(request.ComposeFile))
            throw new BadRequestException($"'{request.ComposeFile}' is not a Compose file path of the repository.");
        if (!string.IsNullOrWhiteSpace(request.Strategy) && request.Strategy is not (ProductionStrategy.BlueGreen or ProductionStrategy.Replace))
            throw new BadRequestException(
                $"'{request.Strategy}' is not a deployment strategy: use '{ProductionStrategy.BlueGreen}' or '{ProductionStrategy.Replace}'.");
    }

    /// <summary>Allocates the ports, then writes the planned library entries and vault secrets.</summary>
    private async Task WriteAsync(Writes writes, int libraryId, int vaultId, int? serverId,
        Dictionary<string, VariableEntryDto> held, CancellationToken ct)
    {
        if (writes.PortKeys.Count > 0)
            await AllocatePortsAsync(libraryId, serverId, held, writes, ct).ConfigureAwait(false);
        if (writes.LibraryAdds.Count > 0)
            await libraries.ImportEntriesAsync(libraryId, writes.LibraryAdds, ct).ConfigureAwait(false);
        foreach (var (entryId, key, value) in writes.LibraryUpdates)
            await libraries.UpdateEntryAsync(
                libraryId, entryId, new UpdateVariableEntryRequest { Key = key, Value = value }, ct).ConfigureAwait(false);
        if (writes.VaultAdds.Count > 0)
            await vaults.ImportSecretsAsync(vaultId, writes.VaultAdds, ct).ConfigureAwait(false);
    }

    /// <summary>The refused names in the sample the project's configuration repository holds; read from that
    /// repository only, never from the project's source.</summary>
    private async Task<List<ProductionRefusedNameDto>> FindRefusedNamesAsync(int projectId, CancellationToken ct)
    {
        var sample = await configRepository.ReadAsync(projectId, ProductionEnvSampleBuilder.Path, null, null, ct)
            .ConfigureAwait(false);
        return sample is null ? [] : [.. ProductionEnvSampleRepair.Find(sample).Select(Describe)];
    }

    private static ProductionRefusedNameDto Describe(ProductionRefusedName refused) =>
        new()
        {
            File = ProductionEnvSampleBuilder.Path,
            Line = refused.Line,
            Variable = refused.Variable,
            Name = refused.Name,
            ScopedName = refused.ScopedName
        };

    public async Task<PipelineProductionRepairResultDto> RepairEnvSampleAsync(int projectId, CancellationToken ct = default)
    {
        var slug = await PipelineProjectResourceNames.FindSlugAsync(pipelines, projectId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Project {projectId} not found.");
        // The project's own configuration repository only: the read never falls back to the source, the write goes to it.
        var sample = await configRepository.ReadAsync(projectId, ProductionEnvSampleBuilder.Path, null, null, ct)
            .ConfigureAwait(false);
        var refused = sample is null ? [] : ProductionEnvSampleRepair.Find(sample);
        if (sample is null || refused.Count == 0)
            return new PipelineProductionRepairResultDto { File = ProductionEnvSampleBuilder.Path };

        var library = await FindLibraryAsync(projectId, slug + ProdSuffix, ct).ConfigureAwait(false)
            ?? throw new BadRequestException(
                $"The variable library '{slug}{ProdSuffix}' does not exist: create the libraries and vaults first.");
        var held = (await libraries.ExportEntriesAsync(library.Id, ct).ConfigureAwait(false))
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        // Every name is planned first: a conflict refuses the whole repair before anything is written.
        var plans = refused.Select(item => item.Name).Distinct(StringComparer.Ordinal)
            .Select(name => ProductionScopedEntries.Plan(name, held, slug)).ToList();

        // The library first, the sample last: a repair that stops half way is found again by the next one, since the
        // sample still holds the refused names, whereas a mended sample would hide an entry that was never moved.
        var entries = new List<ProductionAppliedEntryDto>();
        foreach (var plan in plans)
            entries.Add(await ApplyScopedEntryAsync(library.Id, plan, ct).ConfigureAwait(false));
        await configRepository.ReplaceAsync(
            projectId, ProductionEnvSampleBuilder.Path, ProductionEnvSampleRepair.Rewrite(sample),
            "fix(config): hold the refused variables of the environment sample under their scoped names via Aetheus", ct)
            .ConfigureAwait(false);

        var mended = refused.Select(Describe).ToList();
        await audit.LogAsync(
            "ProductionEnvSampleRepaired", "Project", projectId,
            $"{ProductionEnvSampleBuilder.Path}: "
                + string.Join(", ", plans.Select(plan => $"{plan.Name} -> {plan.ScopedName} ({plan.Action})")),
            ct).ConfigureAwait(false);
        logger.LogInformation(
            "Environment sample of project {ProjectId} mended: {Count} placeholder(s) moved to their scoped names.",
            projectId, mended.Count);
        return new PipelineProductionRepairResultDto
        {
            File = ProductionEnvSampleBuilder.Path,
            Repaired = true,
            Mended = mended,
            Entries = entries
        };
    }

    private async Task<ProductionAppliedEntryDto> ApplyScopedEntryAsync(
        int libraryId, ScopedEntryPlan plan, CancellationToken ct)
    {
        var applied = new ProductionAppliedEntryDto { Name = plan.ScopedName, Secret = false };
        switch (plan.Action)
        {
            case ScopedEntryAction.Create:
                await libraries.CreateEntryAsync(
                    libraryId, new CreateVariableEntryRequest { Key = plan.ScopedName, Value = plan.Value }, ct).ConfigureAwait(false);
                return applied with
                {
                    Outcome = plan.Value.Length == 0 ? ProductionEntryOutcome.NeedsValue : ProductionEntryOutcome.Created
                };
            case ScopedEntryAction.Rename:
                await libraries.UpdateEntryAsync(
                    libraryId, plan.Refused!.Id,
                    new UpdateVariableEntryRequest { Key = plan.ScopedName, Value = plan.Refused.Value }, ct).ConfigureAwait(false);
                return applied with { Outcome = ProductionEntryOutcome.Renamed };
            case ScopedEntryAction.Merge:
                await libraries.UpdateEntryAsync(
                    libraryId, plan.Scoped!.Id,
                    new UpdateVariableEntryRequest { Key = plan.ScopedName, Value = plan.Refused!.Value }, ct).ConfigureAwait(false);
                await libraries.DeleteEntryAsync(libraryId, plan.Refused.Id, ct).ConfigureAwait(false);
                return applied with { Outcome = ProductionEntryOutcome.Renamed };
            case ScopedEntryAction.DropRefused:
                await libraries.DeleteEntryAsync(libraryId, plan.Refused!.Id, ct).ConfigureAwait(false);
                return applied with { Outcome = ProductionEntryOutcome.Kept };
            default:
                return applied with { Outcome = ProductionEntryOutcome.Kept };
        }
    }

    /// <summary>The servers the production deployment can target (the project's port targets and its production
    /// environment's servers), and the one to preselect: the environment's only server, else the only server.</summary>
    private async Task<(List<ProductionServerDto> Servers, string? DefaultServer)> ResolveServersAsync(
        int projectId, VariableLibraryDto? library, ProductionServerScope scope, CancellationToken ct)
    {
        var projectServers = library is null
            ? []
            : (await ports.GetTargetsAsync(library.Id, ct).ConfigureAwait(false)).Servers
                .Select(server => new ProductionServerDto { Id = server.ServerId, Name = server.ServerName }).ToList();
        var environmentServers = await ProductionEnvironmentServersAsync(projectId, ct).ConfigureAwait(false);
        var servers = projectServers.Concat(environmentServers)
            .DistinctBy(server => server.Id)
            .OrderBy(server => server.Name, StringComparer.Ordinal)
            .ToList();
        if (servers.Count == 0 && scope.AdministeredServerIds is not { Count: 0 })
        {
            // Recette R-77: a new project has no server yet. The servers the user administers (a launch needs Admin on its target) are offered instead of leaving
            // the deployment without one; the one chosen is attached when the setup is applied.
            var candidates = (await setupServers.ListServersAsync(scope.AdministeredServerIds, ct).ConfigureAwait(false))
                .Select(server => new ProductionServerDto { Id = server.Id, Name = server.Name, Candidate = true })
                .ToList();
            return (candidates, candidates.Count == 1 ? candidates[0].Name : null);
        }
        var defaultServer = environmentServers.Count == 1 ? environmentServers[0].Name
            : environmentServers.Count == 0 && servers.Count == 1 ? servers[0].Name
            : null;
        return (servers, defaultServer);
    }

    private static ProjectConfigRepositoryDto DescribeConfiguration(GitLightRepoDto configuration) => new()
    {
        Id = configuration.Id,
        Name = configuration.Name,
        Slug = configuration.Slug,
        DefaultBranch = configuration.DefaultBranch
    };

    private async Task<Inspection> InspectAsync(
        int projectId, string? composeFile, ProductionServerScope scope, CancellationToken ct)
    {
        var slug = await PipelineProjectResourceNames.FindSlugAsync(pipelines, projectId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException($"Project {projectId} not found.");
        var library = await FindLibraryAsync(projectId, slug + ProdSuffix, ct).ConfigureAwait(false);
        var vault = await FindVaultAsync(projectId, slug + ProdSuffix, ct).ConfigureAwait(false);
        var libraryEntries = library is null
            ? []
            : (await libraries.ExportEntriesAsync(library.Id, ct).ConfigureAwait(false))
                .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var vaultKeys = vault is null
            ? []
            : (await vaults.ExportSecretKeysAsync(vault.Id, ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var (servers, defaultServer) = await ResolveServersAsync(projectId, library, scope, ct).ConfigureAwait(false);
        var configuration = await configRepository.FindAsync(projectId, ct).ConfigureAwait(false);
        var configurationDto = configuration is null ? null : DescribeConfiguration(configuration);

        var refused = configuration is null ? [] : await FindRefusedNamesAsync(projectId, ct).ConfigureAwait(false);
        var repository = await FindSourceRepositoryAsync(projectId, ct).ConfigureAwait(false);
        if (repository is null)
            return Unreadable(slug, library, vault, libraryEntries, vaultKeys, servers, defaultServer, configurationDto, refused);

        var branch = string.IsNullOrWhiteSpace(repository.DefaultBranch) ? "main" : repository.DefaultBranch;
        var probe = new PipelineSetupRepositoryProbe(git, logger, repository.Id, branch);
        var (files, _) = await probe.FindComposeFilesAsync(ct).ConfigureAwait(false);
        files.Sort(StringComparer.Ordinal);
        var chosen = composeFile is not null && files.Contains(composeFile, StringComparer.Ordinal)
            ? composeFile
            : ProductionComposeFiles.Choose(files);
        var text = chosen is null ? null : await probe.ReadFileAsync(chosen, ct).ConfigureAwait(false);
        var variables = text is null ? [] : ComposeVariableExtractor.Extract(text);
        // R-66, R-68: the repository's own environment sample and Apache templates are rendered at each deployment too.
        var templates = await probe.ReadProductionTemplatesAsync(ct).ConfigureAwait(false);
        var plans = WithDomain(ProductionTemplateVariables.Extend(ProductionVariableClassifier.Classify(variables, slug), templates, slug));

        var analysis = new PipelineProductionAnalysisDto
        {
            Readable = true,
            Branch = branch,
            ComposeFiles = files,
            ComposeFile = chosen,
            Servers = servers,
            DefaultServer = defaultServer,
            Variables = [.. plans.Where(Shown).Select(plan => Describe(plan, libraryEntries, vaultKeys))],
            DeclaresFrontPort = plans.Any(plan => plan.Name == ProductionVariableClassifier.FrontPort),
            DeclaresBackPort = plans.Any(plan => plan.Name == ProductionVariableClassifier.BackPort),
            DeclaresBlueGreen = ProductionVariableClassifier.DeclaresBlueGreen(variables),
            ConfigRepository = configurationDto,
            RefusedEnvNames = refused
        };
        return new Inspection(analysis, plans, slug, library, vault, libraryEntries, vaultKeys);
    }

    private static Inspection Unreadable(
        string slug, VariableLibraryDto? library, VaultDto? vault, Dictionary<string, VariableEntryDto> entries,
        HashSet<string> vaultKeys, List<ProductionServerDto> servers, string? defaultServer,
        ProjectConfigRepositoryDto? configuration, List<ProductionRefusedNameDto> refused) =>
        new(new PipelineProductionAnalysisDto
        {
            Readable = false,
            Servers = servers,
            DefaultServer = defaultServer,
            ConfigRepository = configuration,
            RefusedEnvNames = refused
        },
            [], slug, library, vault, entries, vaultKeys);

    /// <summary>The domain the production hosts are built on (<c>APP_HOST</c>, <c>API_HOST</c> of the deployment template).</summary>
    internal const string DomainKey = "DOMAIN";

    /// <summary>The production template builds every host, virtual host and certificate on <c>DOMAIN</c> of the
    /// library <c>prod</c>, which only the operator knows: it is asked here with the Compose file's own values
    /// (recette R-5), so the deployment does not start on an empty domain. It is not a value of the environment
    /// file.</summary>
    private static List<ProductionVariablePlan> WithDomain(IEnumerable<ProductionVariablePlan> plans) =>
    [
        .. plans.Where(plan => plan.Name != DomainKey),
        new(DomainKey, ProductionVariableKind.NeedsValue, false, null)
    ];

    /// <summary>The names the project's production environment goes by, the platform's own first.</summary>
    private static readonly string[] ProductionEnvironmentNames = ["prod", "production"];

    /// <summary>The servers of the project's own production environment (recette R-21): the deployment's default
    /// server is the one the environment was given, not a server the user has to type again.</summary>
    private async Task<List<ProductionServerDto>> ProductionEnvironmentServersAsync(int projectId, CancellationToken ct)
    {
        foreach (var name in ProductionEnvironmentNames)
        {
            var environment = await pipelines.FindEnvironmentByNameAsync(name, projectId, ct).ConfigureAwait(false);
            if (environment?.ProjectId != projectId) continue;
            return
            [
                .. environment.Servers
                    .Where(link => link.Server is not null)
                    .Select(link => new ProductionServerDto { Id = link.ServerId, Name = link.Server!.Name })
            ];
        }
        return [];
    }

    /// <summary>What the operator is told about: a variable the deployment provides itself is not shown.</summary>
    private static bool Shown(ProductionVariablePlan plan) => plan.Kind != ProductionVariableKind.Provided;

    private static ProductionVariableDto Describe(
        ProductionVariablePlan plan, Dictionary<string, VariableEntryDto> libraryEntries, HashSet<string> vaultKeys) =>
        new()
        {
            Name = plan.Name,
            Kind = plan.Kind,
            Secret = plan.Secret,
            Exists = plan.Secret ? vaultKeys.Contains(plan.Key) : libraryEntries.TryGetValue(plan.Key, out var entry) && entry.Value.Length > 0,
            DefaultValue = plan.Secret ? null : plan.DefaultValue
        };

    private async Task<GitLightRepoDto?> FindSourceRepositoryAsync(int projectId, CancellationToken ct)
    {
        // The project's own repository (internal, or the mirror of its external source): never the configuration
        // repository, never an external source attached beside it.
        var repositories = (await git.GetRepositoriesAsync(projectId, ct).ConfigureAwait(false))
            .Where(item => !item.IsAdditionalSource && !item.IsConfiguration)
            .ToList();
        var repository = repositories.Find(item => !item.IsEmpty);
        if (repository is null && repositories.Count > 0)
            logger.LogInformation("Project {ProjectId} has no commit to read its Compose file from yet.", projectId);
        return repository;
    }

    private async Task<VariableLibraryDto?> FindLibraryAsync(int projectId, string name, CancellationToken ct) =>
        (await libraries.GetLibrariesAsync(
            projectId, request: new PaginationRequest { Page = 1, PageSize = ResourceSearchPageSize, Search = name }, ct: ct)
            .ConfigureAwait(false)).Items
        .FirstOrDefault(item => item.ProjectId == projectId && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    private async Task<VaultDto?> FindVaultAsync(int projectId, string name, CancellationToken ct) =>
        (await vaults.GetVaultsAsync(
            projectId, request: new PaginationRequest { Page = 1, PageSize = ResourceSearchPageSize, Search = name }, ct: ct)
            .ConfigureAwait(false)).Items
        .FirstOrDefault(item => item.ProjectId == projectId && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Decides what one variable becomes. Nothing existing is overwritten: an entry already there is
    /// kept, except a library entry that is still empty while the operator gave its value now.</summary>
    private static void Plan(
        ProductionVariablePlan plan, Inspection inspection, Dictionary<string, string> values, Writes writes)
    {
        if (plan.Kind is ProductionVariableKind.Provided or ProductionVariableKind.Optional) return;

        var exists = plan.Secret ? inspection.VaultKeys.Contains(plan.Key) : inspection.LibraryEntries.ContainsKey(plan.Key);
        var emptyEntry = !plan.Secret && inspection.LibraryEntries.TryGetValue(plan.Key, out var held) && held.Value.Length == 0;
        var given = values.TryGetValue(plan.Name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        if (plan.Kind == ProductionVariableKind.Port)
            PlanPort(plan, exists, emptyEntry, writes);
        else if (exists)
            PlanExisting(plan, inspection, emptyEntry, given, writes);
        else
            PlanMissing(plan, given, writes);
    }

    /// <summary>A port the library already holds with a value is never reserved again. One it holds empty (a first run with no
    /// server left it so) is allocated now: the port allocation fills an empty key and refuses one that carries a value.</summary>
    private static void PlanPort(ProductionVariablePlan plan, bool exists, bool emptyEntry, Writes writes)
    {
        if (!exists || emptyEntry) writes.PortKeys.Add(plan.Key);
        else writes.Entries.Add(Entry(plan, ProductionEntryOutcome.Kept));
    }

    private static void PlanExisting(
        ProductionVariablePlan plan, Inspection inspection, bool emptyEntry, string? given, Writes writes)
    {
        if (emptyEntry && given is not null)
        {
            writes.LibraryUpdates.Add((inspection.LibraryEntries[plan.Key].Id, plan.Key, given));
            writes.Entries.Add(Entry(plan, ProductionEntryOutcome.Created));
        }
        else
            writes.Entries.Add(Entry(plan, emptyEntry ? ProductionEntryOutcome.NeedsValue : ProductionEntryOutcome.Kept));
    }

    private static void PlanMissing(ProductionVariablePlan plan, string? given, Writes writes)
    {
        switch (plan.Kind)
        {
            case ProductionVariableKind.GeneratedSecret:
                writes.VaultAdds.Add(new CreateVaultSecretRequest { Key = plan.Key, Value = VaultSecretGenerator.Generate(plan.Name) });
                writes.Entries.Add(Entry(plan, ProductionEntryOutcome.Generated));
                break;
            case ProductionVariableKind.DefaultValue:
                writes.LibraryAdds.Add(new CreateVariableEntryRequest { Key = plan.Key, Value = plan.DefaultValue ?? string.Empty });
                writes.Entries.Add(Entry(plan, ProductionEntryOutcome.Created));
                break;
            default:
                // A value only the operator knows: written when he gave it, otherwise created empty (never silent,
                // the deployment stops on it by name).
                if (plan.Secret)
                    writes.VaultAdds.Add(new CreateVaultSecretRequest { Key = plan.Key, Value = given ?? string.Empty });
                else
                    writes.LibraryAdds.Add(new CreateVariableEntryRequest { Key = plan.Key, Value = given ?? string.Empty });
                writes.Entries.Add(Entry(plan, given is null ? ProductionEntryOutcome.NeedsValue : ProductionEntryOutcome.Created));
                break;
        }
    }

    private static ProductionAppliedEntryDto Entry(ProductionVariablePlan plan, ProductionEntryOutcome outcome) =>
        new() { Name = plan.Key, Secret = plan.Secret, Outcome = outcome };

    /// <summary>What a blue-green deployment needs on the host: the Apache Define prefix of its cutover and the two files
    /// (the upstream and its link) the cutover maintains. Written once, never overwritten.</summary>
    private static void PlanUpstream(Inspection inspection, Writes writes)
    {
        var define = ProductionVariableClassifier.UpstreamDefine(inspection.Slug);
        PlanLibraryEntry(inspection, writes, UpstreamDefineKey, define);
        PlanLibraryEntry(inspection, writes, UpstreamConfKey, $"{SitesAvailable}/000-{inspection.Slug}-upstream.conf");
        PlanLibraryEntry(inspection, writes, UpstreamLinkKey, $"{SitesEnabled}/000-{inspection.Slug}-upstream.conf");
        PlanLibraryEntry(inspection, writes, ReloadHelperKey, ReloadHelper);
    }

    private static void PlanLibraryEntry(Inspection inspection, Writes writes, string key, string value)
    {
        var applied = new ProductionAppliedEntryDto { Name = key, Secret = false };
        if (inspection.LibraryEntries.ContainsKey(key))
        {
            writes.Entries.Add(applied with { Outcome = ProductionEntryOutcome.Kept });
            return;
        }

        writes.LibraryAdds.Add(new CreateVariableEntryRequest { Key = key, Value = value });
        writes.Entries.Add(applied with { Outcome = ProductionEntryOutcome.Created });
    }

    /// <summary>Reserves each port in the registry of the chosen server. With no server, or when the registry has no
    /// free port, each entry is created empty for the library page to allocate later, and is reported, never hidden. An
    /// entry the library already holds empty is not created a second time.</summary>
    private async Task AllocatePortsAsync(
        int libraryId, int? serverId, Dictionary<string, VariableEntryDto> held, Writes writes, CancellationToken ct)
    {
        var allocated = false;
        if (serverId is { } id)
        {
            try
            {
                await ports.AllocateIntoLibraryAsync(libraryId, id, writes.PortKeys, ct).ConfigureAwait(false);
                allocated = true;
            }
            catch (Exception exception) when (exception is BadRequestException or ConflictException)
            {
                // Mandatory fallback: a full registry must not abort the whole setup. The service already released
                // what it had reserved, so empty entries are the honest state, and the outcome says so.
                logger.LogWarning(exception, "Ports could not be allocated for library {LibraryId}.", libraryId);
            }
        }

        if (!allocated)
            writes.LibraryAdds.AddRange(
                writes.PortKeys.Where(key => !held.ContainsKey(key))
                    .Select(key => new CreateVariableEntryRequest { Key = key, Value = string.Empty }));
        var outcome = allocated ? ProductionEntryOutcome.Allocated : ProductionEntryOutcome.PortUnavailable;
        writes.Entries.AddRange(
            writes.PortKeys.Select(key => new ProductionAppliedEntryDto { Name = key, Secret = false, Outcome = outcome }));
    }

    /// <summary>The versioned files of the configuration repository: the environment sample of the Compose file and
    /// the generic Apache virtual hosts. The repository never overwrites a file it already holds.</summary>
    private static Dictionary<string, string> ConfigFiles(
        IEnumerable<ProductionVariablePlan> plans, bool declaresApi, bool blueGreen)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProductionEnvSampleBuilder.Path] = ProductionEnvSampleBuilder.Build(plans.Where(plan => plan.Name != DomainKey))
        };
        foreach (var (path, content) in ProjectConfigFiles.GenericApacheFiles(declaresApi, blueGreen))
            files[path] = content;
        return files;
    }
}
