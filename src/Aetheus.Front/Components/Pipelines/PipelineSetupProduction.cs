// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>What the wizard writes into the production pipeline (ADR-057): the Compose file, the strategy, the
/// server the pipeline is bound to and the servers it may be run on. A value the wizard does not know is null and is
/// not written: when the repository could not be read (recette R-80, a project with no commit yet), the Compose file
/// and the strategy stay null while the servers, which come from the project and not from the file, are kept. Whether
/// the API has its own host is not written (R-11): it is the template the pipeline extends.</summary>
internal sealed record PipelineSetupProductionInput(
    string? ComposeFile, string? Strategy, string? Server, IReadOnlyList<string>? Servers = null);

/// <summary>What preparing the production setup before the creation produced: what the production pipeline's YAML is written
/// from, the entries still to fill, and whether the repository gave nothing to prepare from (recette R-80: the end of the
/// wizard says that production was not prepared). <paramref name="ConfigFilesAdded"/> (R-38) names the files the preparation
/// created in the project's configuration repository, empty when it created none.</summary>
internal sealed record ProductionPreparation(
    PipelineSetupProductionInput? Input, string Pending, bool NotPrepared, string ConfigFilesAdded = "")
{
    public static ProductionPreparation None { get; } = new(null, string.Empty, false);
}

/// <summary>
/// The production setup the wizard offers on its summary when the production pipeline is selected (ADR-057): the
/// project's Compose file read from its repository, the server it deploys to and the variables that file asks for.
/// It never blocks the creation: a project the wizard cannot read, or with no server, still gets its pipeline, with the
/// servers of the project and nothing claimed of its Compose file, and the end of the wizard says that production was
/// not prepared.
/// R-9: the repository is read on the branch the pipelines are created on (<paramref name="branch"/>), not only on its
/// default branch: a Compose file that only develop holds is found, so the production pipeline gets its
/// <c>COMPOSE_FILE</c> and the library its conventions (<c>APPNAME</c>, <c>DB_USER</c>,
/// <c>DEPLOY_ENV</c>) without a hand edit.
/// R-11: <paramref name="fullStack"/> is the application shape the user chose (true the Full-stack tile, a monolith). It
/// picks the deployment template, and the Compose file only checks it (<see cref="ShapeMismatch"/>); a monolith is limited
/// to the replacement strategy.
/// </summary>
public sealed class PipelineSetupProduction(
    ApiClient api, Action changed, Func<string?>? branch = null, Func<bool?>? fullStack = null, Func<bool>? serverRequired = null)
{
    /// <summary>R-36: the deployment chain is on, so the pipeline it starts is given no server at launch and must carry the
    /// choice as the default of <c>deployServer</c>: the server is a required field of the summary.</summary>
    public bool ServerRequired => serverRequired?.Invoke() == true;

    /// <summary>R-36: the server the production pipeline will carry as the default of <c>deployServer</c>: the one chosen
    /// when the setup will be applied (a Compose file was read), else only one the project already holds, since a server
    /// offered to a project that has none is attached by that setup alone. Null while none is chosen.</summary>
    internal string? EffectiveServer => Ready || Analysis?.Servers.Exists(server => server.Name == Server && !server.Candidate) == true
        ? Server
        : null;

    /// <summary>R-36: the server is required and the project has some to choose from, but none is chosen: the creation is
    /// held back (a pipeline the chain starts would otherwise be refused at its first launch). A server offered to a
    /// project that has none, chosen while no Compose file could be read, counts as chosen: it is attached by the setup
    /// alone, which then has nothing to apply, so the pipeline does not name it (<see cref="EffectiveServer"/>).</summary>
    public bool ServerMissing => ServerRequired && Analysis is { Servers.Count: > 0 } && Server is null;

    /// <summary>The user chose the Full-stack tile: one application serves the interface and the API.</summary>
    public bool Monolith => fullStack?.Invoke() == true;

    /// <summary>R-11: the Compose file read disagrees with the shape chosen: a monolith whose file publishes a back port, or
    /// a front and an API whose file publishes none. Said, never blocking: the shape the user chose decides the template.</summary>
    public bool ShapeMismatch => Analysis is { Readable: true } analysis
        && fullStack?.Invoke() is { } shape && shape == analysis.DeclaresApi;

    private int? _projectId;

    /// <summary>The branch the last reading asked for; the reading is made again when the branch chosen is another.</summary>
    private string? _readBranch;

    /// <summary>The last reading; null while none succeeded.</summary>
    public PipelineProductionAnalysisDto? Analysis { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>The Compose file chosen, the one the analysis proposed until the user picks another.</summary>
    public string? ComposeFile { get; private set; }

    /// <summary>The server the pipeline is bound to; null when the project has none or the user left it empty.</summary>
    public string? Server { get; private set; }

    private string? _strategy;

    /// <summary>Recette R-89 (user decision 2026-10-25): the deployment strategy is a choice the user makes. Recette R-19
    /// (user decision 2026-11-09, replacing the earlier proposals of blue-green and of the strategy the Compose file follows):
    /// until he makes it, the strategy is the replacement of the services, whatever the Compose file declares.
    /// <see cref="StrategyMismatch"/> says when the file does not follow the choice.</summary>
    public string Strategy => Monolith ? ProductionStrategy.Replace : _strategy ?? ProductionStrategy.Replace;

    /// <summary>The Compose file read does not follow the strategy chosen: a blue-green deployment needs it to read the blue
    /// and green ports, a replacement the published ones. Said, never blocking: the file is the project's own and is
    /// adapted outside Aetheus. A monolith has the replacement only (R-11), which its file has to follow.</summary>
    public bool StrategyMismatch => Analysis is { Readable: true } analysis
        && (Strategy == ProductionStrategy.BlueGreen) != analysis.DeclaresBlueGreen;

    public void ChooseStrategy(string? strategy)
    {
        if (Monolith) return;
        _strategy = strategy is ProductionStrategy.BlueGreen or ProductionStrategy.Replace ? strategy : null;
        changed();
    }

    /// <summary>The values typed for the variables only the operator knows, by name.</summary>
    public Dictionary<string, string> Values { get; } = [];

    /// <summary>R-38: a reading was made and the production it describes was never prepared: no Compose file could be read, or the
    /// project holds no configuration repository (the setup creates it, with the Apache files), or a port or a generated secret the
    /// setup creates is still missing. An existing pipeline then offers to complete it instead of being deleted and created again.</summary>
    public bool NeedsPreparation => Analysis is { } analysis
        && (!analysis.Readable || analysis.ComposeFiles.Count == 0 || analysis.ConfigRepository is null
            || analysis.Variables.Exists(variable =>
                variable.Kind is ProductionVariableKind.Port or ProductionVariableKind.GeneratedSecret && !variable.Exists)
            || MissingApacheTemplates.Count > 0);

    /// <summary>R-38: the Apache virtual host templates the deployment applies that the project holds nowhere, even though its
    /// configuration repository exists and its ports and secrets are all there. A monolith has no API virtual host, so the
    /// API's are not asked of it.</summary>
    private List<string> MissingApacheTemplates =>
        Analysis?.MissingApacheTemplates.FindAll(path => !(Monolith && path.Contains("/api-", StringComparison.Ordinal))) ?? [];

    /// <summary>R-38: no Compose file was read, so the full setup cannot be applied, yet the generic Apache templates need only
    /// the shape and the strategy the user chose. They are created in the project's configuration repository unless the user
    /// declines (never silently skipped, never written to the project's source).</summary>
    public bool CreateApacheTemplates { get; private set; } = true;

    public void ChooseCreateApacheTemplates(bool create)
    {
        CreateApacheTemplates = create;
        changed();
    }

    /// <summary>R-38: the Apache templates the configuration repository will be given by this preparation, by path.</summary>
    public List<string> ApacheToCreate => MissingApacheTemplates;

    /// <summary>R-38: the Compose file is not there, but Apache templates are missing: the wizard asks whether to create them.</summary>
    public bool ApacheAsk => Analysis is not null && !Ready && ApacheToCreate.Count > 0;

    /// <summary>R-38: creates the Apache templates alone, in the configuration repository, for the shape and the strategy chosen.</summary>
    public Task<ApiOutcome<ProjectConfigRepositoryDto, ApiError>> ApplyApacheAsync(int projectId) =>
        api.Pipelines.ApplyProductionApacheAsync(new PipelineProductionApacheRequest
        {
            ProjectId = projectId,
            Monolith = Monolith,
            Strategy = Strategy
        });

    /// <summary>R-38: what <see cref="ApplyApacheAsync"/> did when it was asked for and needed; the files it added, by name. Throws
    /// <see cref="InvalidOperationException"/> when the server refuses, so the creation stops before any pipeline is written.</summary>
    internal async Task<string> CreateApacheIfAskedAsync(int projectId, string failed)
    {
        if (!ApacheAsk || !CreateApacheTemplates) return string.Empty;
        var outcome = await ApplyApacheAsync(projectId);
        if (!outcome.IsSuccess)
            throw new InvalidOperationException(RefusalText(outcome, failed));
        return string.Join(", ", outcome.Value!.FilesAdded);
    }

    /// <summary>The analysis read a Compose file: the setup can be applied.</summary>
    public bool Ready => Analysis is { Readable: true } && !string.IsNullOrEmpty(ComposeFile);

    /// <summary>R-7: the repository was read but its environment sample or Apache templates were not, so <c>PORT_FRONT</c> and
    /// <c>PORT_BACK</c> may be missing from the setup. The creation of the pipelines is blocked while it holds, instead of
    /// writing pipelines the deployment would then refuse.</summary>
    public bool TemplatesUnreadable => Analysis is { Readable: true, TemplatesUnreadable: true };

    /// <summary>Recette R-69: the project's source is an external repository and no Compose file could be read from it, so a
    /// production or a QA pipeline created now could never deploy (and the QA would validate nothing). The wizard creates
    /// neither and says why; an internal project that has no commit yet keeps the behavior of recette R-80.</summary>
    public bool ExternalCannotDeploy => Analysis is { ExternalSource: true } && !Ready;

    /// <summary>Why nothing could be read of the external repository; none when the repository was read.</summary>
    public ProductionSourceProblem SourceProblem => Analysis?.SourceProblem ?? ProductionSourceProblem.None;

    /// <summary>The resource that tells the cause of <see cref="ExternalCannotDeploy"/>: the state of the external repository, or
    /// its missing Compose file once it was read.</summary>
    public string ExternalCauseKey => SourceProblem switch
    {
        ProductionSourceProblem.ExternalNotTheSource => "ExternalDeployCauseNotTheSource",
        ProductionSourceProblem.ExternalNotSynchronized => "ExternalDeployCauseNotSynchronized",
        _ => "ExternalDeployCauseNoCompose"
    };

    /// <summary>The repository itself was read, so what is missing is its Compose file; false when nothing could be read
    /// (no commit yet, an unreadable mirror, a failed call).</summary>
    public bool RepositoryRead => Analysis is { Readable: true };

    /// <summary>What the pipeline can be given when the Compose file was not read (recette R-80): the project's servers
    /// and the production environment's one as default, which never came from that file. Null when even the servers
    /// could not be read.</summary>
    internal PipelineSetupProductionInput? WithoutComposeFile() =>
        Analysis is null
            ? null
            : new PipelineSetupProductionInput(null, null, EffectiveServer, [.. ServerChoices(null)]);

    /// <summary>Recette R-77: the servers a deployment pipeline may offer. A candidate (a server the user administers, offered
    /// to a project that has none) is the project's only once attached, so only <paramref name="attached"/> is kept of them.</summary>
    internal IEnumerable<string> ServerChoices(string? attached) =>
        (Analysis?.Servers ?? []).Where(server => !server.Candidate || server.Name == attached).Select(server => server.Name);

    /// <summary>Recette R-75: what a QA pipeline created without the production deployment is given, the Compose file the
    /// reading chose for its disposable stack and nothing else; null when no Compose file was read.</summary>
    internal PipelineSetupProductionInput? ForQaOnly() => Ready ? new PipelineSetupProductionInput(ComposeFile, null, null) : null;

    private Task _reading = Task.CompletedTask;

    /// <summary>Completes once the reading of the repository that is running has ended; at once when none is. The
    /// creation waits on it: asked while the repository was still being read, it used to find the setup "not ready"
    /// and write nothing (recette R-80).</summary>
    public Task SettledAsync() => _reading;

    public Task LoadAsync(int projectId, string? composeFile = null)
    {
        var branchChanged = _readBranch != branch?.Invoke();
        if (_projectId == projectId && composeFile is null && (Analysis is not null || Loading) && !branchChanged) return _reading;
        // Another branch may not hold the file chosen: the analysis then proposes its own.
        return _reading = ReadAsync(projectId, composeFile ?? (branchChanged ? ComposeFile : null));
    }

    /// <summary>Everything the creation needs of the production setup, made just before the pipelines are written: the
    /// reading is made again if the branch chosen changed since (never skipped on a stale one), the setup is applied when
    /// a Compose file was read, and what the pipeline can be given when it was not is returned with
    /// <see cref="ProductionPreparation.NotPrepared"/> set. <paramref name="failed"/> is the sentence used when the server
    /// refuses the setup without saying why. Throws <see cref="InvalidOperationException"/> when the setup is refused.
    /// R-37: <paramref name="fastRelease"/> is the fast release being created. It deploys to the production server from the same
    /// Compose file, so it takes the production path (the Compose file, the strategy, the server and what the file reads) even
    /// when <paramref name="production"/> is false; the production pipeline itself is created only when it is ticked.</summary>
    internal async Task<ProductionPreparation> PrepareAsync(int projectId, bool production, bool qa, string failed, bool fastRelease = false)
    {
        if (!production && !fastRelease)
        {
            // Recette R-75: the QA deploys its disposable stack from the Compose file, production or not.
            if (!qa) return ProductionPreparation.None;
            await LoadAsync(projectId);
            await ReadAgainIfNotReadyAsync(projectId);
            return new ProductionPreparation(ForQaOnly(), string.Empty, false);
        }

        // The reading of the repository may still be running (recette R-80): wait for it, never skip on it.
        await LoadAsync(projectId);
        await ReadAgainIfNotReadyAsync(projectId);
        if (!Ready)
            // The Compose file was not read (a project with no commit yet is a new project): nothing of it is claimed, but the
            // servers of the project and of its production environment do not depend on it, and the end of the wizard says
            // that production was not prepared. R-38: the Apache templates do not depend on that file, so they are still created.
            return new ProductionPreparation(WithoutComposeFile(), string.Empty, true, await CreateApacheIfAskedAsync(projectId, failed));

        var outcome = await ApplyAsync(projectId);
        if (!outcome.IsSuccess) throw new InvalidOperationException(RefusalText(outcome, failed));
        var result = outcome.Value!;
        return new ProductionPreparation(
            new PipelineSetupProductionInput(result.ComposeFile, result.Strategy, result.Server,
                [.. ServerChoices(result.Server)]),
            PendingValues(result), false, string.Join(", ", result.ConfigRepository.FilesAdded));
    }

    /// <summary>R-37: why the server refused a setup call, in its own words: the error payload a refused call carries, then the
    /// text the client kept, and <paramref name="failed"/> only when the server said nothing. Reading only
    /// <c>ErrorMessage</c> showed the bare "Operation failed" for a missing vault.</summary>
    internal static string RefusalText<TValue>(ApiOutcome<TValue, ApiError> outcome, string failed)
        where TValue : class =>
        outcome.Error?.Message is { Length: > 0 } reason ? reason
        : outcome.Error?.Detail is { Length: > 0 } detail ? detail
        : outcome.ErrorMessage is { Length: > 0 } text ? text
        : failed;

    /// <summary>R-6, R-7: a reading that found no Compose file (an empty repository, a failed call) stands until the branch
    /// changes, but the code may have been pushed since (from a terminal, without the first-push offer). Just before the
    /// pipelines are written it is made once more, so a Compose file and the ports of the Apache templates that are there
    /// by now are written the first time instead of being added by hand afterwards.</summary>
    private async Task ReadAgainIfNotReadyAsync(int projectId)
    {
        if (!Ready) await (_reading = ReadAsync(projectId, ComposeFile));
    }

    /// <summary>Reads the project shown again, keeping the Compose file, the server, the strategy and the values chosen:
    /// what the reading depends on (the library <c>prod</c>, its servers and entries) has just been created.</summary>
    public Task ReloadAsync() => _projectId is { } projectId ? _reading = ReadAsync(projectId, ComposeFile) : _reading;

    private async Task ReadAsync(int projectId, string? composeFile)
    {
        _projectId = projectId;
        _readBranch = branch?.Invoke();
        Loading = true;
        changed();
        try
        {
            Analysis = await api.Pipelines.AnalyzeProductionAsync(projectId, composeFile, _readBranch);
            ComposeFile = Analysis?.ComposeFile;
            // A choice the operator already made is kept; otherwise the server of the production environment, or
            // the only server (recette R-21). With several and no environment server, none is guessed.
            Server = Analysis?.Servers switch
            {
                { } servers when servers.Exists(item => item.Name == Server) => Server,
                _ => Analysis?.DefaultServer
            };
        }
        catch (Exception exception) when (RunParametersReading.IsTransportFailure(exception))
        {
            // Nothing is read, so nothing is claimed: the panel says the repository could not be read.
            Analysis = null;
            ComposeFile = null;
            _projectId = null;
        }
        finally
        {
            Loading = false;
            changed();
        }
    }

    /// <summary>The mending of the environment sample is running.</summary>
    public bool Repairing { get; private set; }

    /// <summary>What the last mending did; null while none was done for the project shown.</summary>
    public PipelineProductionRepairResultDto? Repaired { get; private set; }

    /// <summary>The last mending was refused or failed.</summary>
    public bool RepairFailed { get; private set; }

    /// <summary>Why it failed, in the server's own words; null when the server gave none.</summary>
    public string? RepairError { get; private set; }

    /// <summary>Recette R-5: mends the environment sample the project's configuration repository already holds, then
    /// reads the project again so the warning is shown only while a refused name remains.</summary>
    public async Task RepairAsync()
    {
        if (_projectId is not { } projectId || Repairing) return;
        Repairing = true;
        RepairFailed = false;
        RepairError = null;
        changed();
        try
        {
            var outcome = await api.Pipelines.RepairProductionEnvSampleAsync(projectId);
            if (outcome.IsSuccess)
            {
                Repaired = outcome.Value;
                _projectId = null;
                await LoadAsync(projectId, ComposeFile);
            }
            else
            {
                RepairFailed = true;
                RepairError = outcome.Error?.Message is { Length: > 0 } reason ? reason : outcome.ErrorMessage;
            }
        }
        finally
        {
            Repairing = false;
            changed();
        }
    }

    public Task ChooseComposeFileAsync(string? composeFile) =>
        string.IsNullOrEmpty(composeFile) || _projectId is not { } projectId
            ? Task.CompletedTask
            : LoadAsync(projectId, composeFile);

    public void ChooseServer(string? server)
    {
        Server = string.IsNullOrEmpty(server) ? null : server;
        changed();
    }

    public void SetValue(string name, string value)
    {
        Values[name] = value;
        changed();
    }

    /// <summary>Forgets what was read: another project, or a selection without production.</summary>
    public void Reset()
    {
        _projectId = null;
        _readBranch = null;
        Analysis = null;
        ComposeFile = null;
        Server = null;
        _strategy = null;
        CreateApacheTemplates = true;
        Repaired = null;
        RepairFailed = false;
        RepairError = null;
        Values.Clear();
    }

    /// <summary>Recette R-5: the entries the production pipeline reads that the setup left without a value (none typed,
    /// or no free port), named so the end of the wizard says which ones remain to fill. Empty when nothing is missing.</summary>
    public static string PendingValues(PipelineProductionApplyResultDto result) =>
        string.Join(", ", result.Entries
            .Where(entry => entry.Outcome is ProductionEntryOutcome.NeedsValue or ProductionEntryOutcome.PortUnavailable)
            .Select(entry => entry.Name)
            .Order(StringComparer.Ordinal));

    /// <summary>The creation of everything the production pipeline reads. Only the values of the variables that
    /// need one are sent, never anything read back.</summary>
    public Task<ApiOutcome<PipelineProductionApplyResultDto, ApiError>> ApplyAsync(int projectId) =>
        api.Pipelines.ApplyProductionAsync(new PipelineProductionApplyRequest
        {
            ProjectId = projectId,
            ComposeFile = ComposeFile ?? string.Empty,
            Branch = _readBranch,
            Server = Server ?? string.Empty,
            Strategy = Strategy,
            Monolith = Monolith,
            Values = Values
                .Where(pair => Analysis?.Variables.Exists(variable =>
                    variable.Name == pair.Key && variable.Kind == ProductionVariableKind.NeedsValue) == true)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        });
}
