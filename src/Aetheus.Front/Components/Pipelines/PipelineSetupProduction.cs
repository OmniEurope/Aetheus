// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>What the wizard writes into the production pipeline (ADR-057): the Compose file, the strategy, the API, the
/// server the pipeline is bound to and the servers it may be run on. A value the wizard does not know is null and is
/// not written: when the repository could not be read (recette R-80, a project with no commit yet), the Compose file,
/// the strategy and the API stay null while the servers, which come from the project and not from the file, are kept.</summary>
internal sealed record PipelineSetupProductionInput(
    string? ComposeFile, string? Strategy, bool? HasApi, string? Server, IReadOnlyList<string>? Servers = null);

/// <summary>
/// The production setup the wizard offers on its summary when the production pipeline is selected (ADR-057): the
/// project's Compose file read from its repository, the server it deploys to and the variables that file asks for.
/// It never blocks the creation: a project the wizard cannot read, or with no server, still gets its pipeline, with the
/// servers of the project and nothing claimed of its Compose file, and the end of the wizard says that production was
/// not prepared.
/// </summary>
public sealed class PipelineSetupProduction(ApiClient api, Action changed)
{
    private int? _projectId;

    /// <summary>The last reading; null while none succeeded.</summary>
    public PipelineProductionAnalysisDto? Analysis { get; private set; }

    public bool Loading { get; private set; }

    /// <summary>The Compose file chosen, the one the analysis proposed until the user picks another.</summary>
    public string? ComposeFile { get; private set; }

    /// <summary>The server the pipeline is bound to; null when the project has none or the user left it empty.</summary>
    public string? Server { get; private set; }

    private string? _strategy;

    /// <summary>Recette R-89 (user decision 2026-10-25): the deployment strategy is a choice the user makes. Recette R-23
    /// (W8, user decision 2026-10-26, option a): until he makes it, the one proposed is the one the Compose file read
    /// follows (its blue and green ports, else its published ones), so a file laid out for a replacement is no longer
    /// proposed a blue-green deployment it cannot run. Blue-green, which the user prefers, stays the proposal when no
    /// Compose file was read. <see cref="StrategyMismatch"/> says when the file does not follow the choice.</summary>
    public string Strategy => _strategy ?? (Analysis is { Readable: true, ComposeFile: not null } analysis
        ? analysis.Strategy
        : ProductionStrategy.BlueGreen);

    /// <summary>The Compose file read does not follow the strategy chosen: a blue-green deployment needs it to read the blue
    /// and green ports, a replacement the published ones. Said, never blocking: the file is the project's own and is
    /// adapted outside Aetheus.</summary>
    public bool StrategyMismatch => Analysis is { Readable: true } analysis
        && (Strategy == ProductionStrategy.BlueGreen) != analysis.DeclaresBlueGreen;

    public void ChooseStrategy(string? strategy)
    {
        _strategy = strategy is ProductionStrategy.BlueGreen or ProductionStrategy.Replace ? strategy : null;
        changed();
    }

    /// <summary>The values typed for the variables only the operator knows, by name.</summary>
    public Dictionary<string, string> Values { get; } = [];

    /// <summary>The analysis read a Compose file: the setup can be applied.</summary>
    public bool Ready => Analysis is { Readable: true } && !string.IsNullOrEmpty(ComposeFile);

    /// <summary>The repository itself was read, so what is missing is its Compose file; false when nothing could be read
    /// (no commit yet, an unreadable mirror, a failed call).</summary>
    public bool RepositoryRead => Analysis is { Readable: true };

    /// <summary>What the pipeline can be given when the Compose file was not read (recette R-80): the project's servers
    /// and the production environment's one as default, which never came from that file. Null when even the servers
    /// could not be read.</summary>
    internal PipelineSetupProductionInput? WithoutComposeFile() =>
        Analysis is null
            ? null
            : new PipelineSetupProductionInput(null, null, null,
                Analysis.Servers.Exists(server => server.Name == Server && !server.Candidate) ? Server : null,
                [.. ServerChoices(null)]);

    /// <summary>Recette R-77: the servers a deployment pipeline may offer. A candidate (a server the user administers, offered
    /// to a project that has none) is the project's only once attached, so only <paramref name="attached"/> is kept of them.</summary>
    internal IEnumerable<string> ServerChoices(string? attached) =>
        (Analysis?.Servers ?? []).Where(server => !server.Candidate || server.Name == attached).Select(server => server.Name);

    /// <summary>Recette R-75: what a QA pipeline created without the production deployment is given, the Compose file the
    /// reading chose for its disposable stack and nothing else; null when no Compose file was read.</summary>
    internal PipelineSetupProductionInput? ForQaOnly() => Ready ? new PipelineSetupProductionInput(ComposeFile, null, null, null) : null;

    private Task _reading = Task.CompletedTask;

    /// <summary>Completes once the reading of the repository that is running has ended; at once when none is. The
    /// creation waits on it: asked while the repository was still being read, it used to find the setup "not ready"
    /// and write nothing (recette R-80).</summary>
    public Task SettledAsync() => _reading;

    public Task LoadAsync(int projectId, string? composeFile = null)
    {
        if (_projectId == projectId && composeFile is null && Analysis is not null) return _reading;
        return _reading = ReadAsync(projectId, composeFile);
    }

    /// <summary>Reads the project shown again, keeping the Compose file, the server, the strategy and the values chosen:
    /// what the reading depends on (the library <c>prod</c>, its servers and entries) has just been created.</summary>
    public Task ReloadAsync() => _projectId is { } projectId ? _reading = ReadAsync(projectId, ComposeFile) : _reading;

    private async Task ReadAsync(int projectId, string? composeFile)
    {
        _projectId = projectId;
        Loading = true;
        changed();
        try
        {
            Analysis = await api.Pipelines.AnalyzeProductionAsync(projectId, composeFile);
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
        Analysis = null;
        ComposeFile = null;
        Server = null;
        _strategy = null;
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
            Server = Server ?? string.Empty,
            Strategy = Strategy,
            Values = Values
                .Where(pair => Analysis?.Variables.Exists(variable =>
                    variable.Name == pair.Key && variable.Kind == ProductionVariableKind.NeedsValue) == true)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        });
}
