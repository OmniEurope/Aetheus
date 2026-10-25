// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>What the wizard writes into the production pipeline once the production setup was applied (ADR-057):
/// the Compose file, the strategy, the API, the server the pipeline is bound to and the servers it may be run on.</summary>
internal sealed record PipelineSetupProductionInput(
    string ComposeFile, string Strategy, bool HasApi, string? Server, IReadOnlyList<string>? Servers = null);

/// <summary>
/// The production setup the wizard offers on its summary when the production pipeline is selected (ADR-057): the
/// project's Compose file read from its repository, the server it deploys to and the variables that file asks for.
/// It never blocks the creation: a project the wizard cannot read, or with no server, still gets its pipeline, and
/// the readiness check already says what is missing.
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

    /// <summary>The values typed for the variables only the operator knows, by name.</summary>
    public Dictionary<string, string> Values { get; } = [];

    /// <summary>The analysis read a Compose file: the setup can be applied.</summary>
    public bool Ready => Analysis is { Readable: true } && !string.IsNullOrEmpty(ComposeFile);

    public async Task LoadAsync(int projectId, string? composeFile = null)
    {
        if (_projectId == projectId && composeFile is null && Analysis is not null) return;
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
        catch (HttpRequestException)
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
            Values = Values
                .Where(pair => Analysis?.Variables.Exists(variable =>
                    variable.Name == pair.Key && variable.Kind == ProductionVariableKind.NeedsValue) == true)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        });
}
