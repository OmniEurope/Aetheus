// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.ExternalRepos;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>R-38: on an existing production pipeline whose production was never prepared (the wizard could not read the Compose
/// file), runs the same preparation the wizard runs (<see cref="PipelineSetupProduction"/>, the endpoints of the setup, so the
/// same authorization) and adds to the definition the Compose file and the strategy it was created without.</summary>
public partial class PipelineProductionCompletion
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineDto Pipeline { get; set; } = default!;

    /// <summary>Raised once the production is prepared and the definition updated.</summary>
    [Parameter] public EventCallback Completed { get; set; }

    private PipelineSetupProduction? _setup;
    private int? _loadedPipelineId;
    private string _composeTyped = string.Empty;
    private bool _busy;
    private string? _failure;
    private string? _done;

    /// <summary>R-27: kept for the life of the component, so a failed creation or synchronization is retried by the next click.</summary>
    private PipelineSetupMirrorSync Mirror => field ??= new PipelineSetupMirrorSync(Api, L, () => { });

    /// <summary>R-27: the address the project names while nothing could be read of its repository, to know whether it is an
    /// external address nobody mirrors; null once the mirror exists or when the project names none.</summary>
    private string? _address;

    /// <summary>R-27: the project names an external repository address and has no mirror: nothing reads it until the mirror is
    /// created, which the completion does itself.</summary>
    private bool AddressUnmirrored => _setup?.Analysis is { Readable: false, ExternalSource: false }
        && ExternalRepositoryAddress.IsUnmirrored(_address);

    private string? TemplateName => PipelineTemplateReferenceHelper.Parse(Pipeline.YamlDefinition)?.Name;

    private bool IsProductionPipeline => TemplateName is { } name && PipelineSetupRoles.IsDeployProdTemplate(name);

    private string? Branch => _setup?.Analysis?.Branch is { Length: > 0 } read ? read : Pipeline.SourceBranch;

    private IReadOnlyList<OmniOption<string>> ServerOptions =>
        (_setup?.Analysis?.Servers ?? []).ToOptions(item => item.Name, item => item.Name);

    private string? ServerValue => _setup?.Server;

    /// <summary>Several servers and none known as the production one: the setup reserves the ports on the server chosen.</summary>
    private bool ServerChoiceNeeded => _setup is { Analysis.Servers.Count: > 1, Server: null };

    /// <summary>What is missing, said by name: the repository (no commit to read), the Compose file on the branch read, or the
    /// rest of the preparation (configuration repository, ports, secrets) when the Compose file is there.</summary>
    private string Cause => _setup switch
    {
        null => string.Empty,
        { Analysis.ExternalSource: true, Ready: false } => L[_setup.ExternalCauseKey].Value,
        { Analysis.Readable: false } when AddressUnmirrored => L["PipelineProdCompletionAddressNotMirrored"].Value,
        { Analysis.Readable: false } => string.Format(L["PipelineProdCompletionUnreadable"], Branch),
        { Ready: false } => string.Format(L["PipelineProdCompletionNoCompose"], Branch),
        _ => L["PipelineProdCompletionIncomplete"].Value
    };

    protected override async Task OnParametersSetAsync()
    {
        if (!IsProductionPipeline || Pipeline.ProjectId is not { } projectId)
        {
            _setup = null;
            return;
        }
        if (_loadedPipelineId == Pipeline.Id) return;
        _loadedPipelineId = Pipeline.Id;
        var setup = new PipelineSetupProduction(
            Api, () => _ = InvokeAsync(StateHasChanged), () => Pipeline.SourceBranch, () => TemplateName == PipelineSetupRoles.DeployProdTemplate);
        // The strategy the pipeline already carries is the one prepared for, not the one the Compose file suggests.
        if (PipelineProductionYaml.ReadVariable(Pipeline.YamlDefinition, PipelineProductionYaml.StrategyKey) is { } strategy)
            setup.ChooseStrategy(strategy);
        _setup = setup;
        await setup.LoadAsync(projectId);
        if (setup.Analysis is { Readable: false, ExternalSource: false })
        {
            _address = await Mirror.ReadUnmirroredAddressAsync(projectId);
        }
    }

    private void ChooseServer(string? server) => _setup?.ChooseServer(server);

    private async Task CompleteAsync()
    {
        if (_setup is null || Pipeline.ProjectId is not { } projectId || _busy) return;
        _busy = true;
        _failure = null;
        try
        {
            // R-23: an external repository is read through its mirror; it is synchronized before the preparation reads it again.
            // R-27: an external address nobody mirrors gets its mirror created first (the same call as the project page).
            if (_setup.Analysis is { Readable: false } && (_setup.Analysis.ExternalSource || AddressUnmirrored))
            {
                Mirror.Forget();
                await Mirror.EnsureAsync(projectId, _address);
                if (Mirror.Failure is not null)
                {
                    _failure = Mirror.Failure;
                    return;
                }
                _address = null;
                await _setup.ReloadAsync();
            }
            _failure = await PrepareAsync(_setup, projectId);
            if (_failure is null)
                await _setup.ReloadAsync();
            // R-27: a click always has a visible outcome: a failure that is the cause already shown is said as retried.
            else if (_failure == Cause)
                _failure = string.Format(L["PipelineProdCompletionStillBlocked"], _failure);
        }
        finally
        {
            _busy = false;
        }
        if (_failure is null) await Completed.InvokeAsync();
    }

    /// <summary>Runs the preparation; returns why it could not be done, null once it is done and the definition updated.</summary>
    private async Task<string?> PrepareAsync(PipelineSetupProduction setup, int projectId)
    {
        var typed = _composeTyped.Trim();
        if (typed.Length > 0 && setup.ComposeFile != typed)
        {
            await setup.ChooseComposeFileAsync(typed);
            if (setup.ComposeFile != typed)
                return string.Format(L["PipelineProdCompletionComposeNotFound"], typed, Branch);
        }
        if (!setup.Ready)
        {
            // R-38: the Apache templates do not need the Compose file; they are created now, and what still blocks is said.
            try
            {
                var added = await setup.CreateApacheIfAskedAsync(projectId, L["OperationFailed"]);
                if (added.Length > 0)
                {
                    await setup.ReloadAsync();
                    Toast.Info("ProductionConfigFilesAdded", "ProductionConfigFilesAddedDetail", added);
                }
            }
            catch (InvalidOperationException exception)
            {
                return exception.Message;
            }
            return Cause;
        }
        if (ServerChoiceNeeded) return L["PipelineSetupProdServerRequiredError"].Value;

        var outcome = await setup.ApplyAsync(projectId);
        if (!outcome.IsSuccess)
            return outcome.Error?.Message is { Length: > 0 } reason ? reason : outcome.ErrorMessage ?? L["OperationFailed"].Value;
        var result = outcome.Value!;

        var yaml = PipelineProductionYaml.WithMissingVariables(Pipeline.YamlDefinition, result.ComposeFile, result.Strategy);
        if (yaml != Pipeline.YamlDefinition)
        {
            var saved = await Api.Pipelines.UpdatePipelineAsync(Pipeline.Id, new UpdatePipelineRequest
            {
                Name = Pipeline.Name,
                Description = Pipeline.Description,
                YamlDefinition = yaml,
                ProjectId = Pipeline.ProjectId,
                SourceBranch = Pipeline.SourceBranch,
                SourceRepositoryId = Pipeline.SourceRepositoryId,
                EnvironmentId = Pipeline.EnvironmentId,
                ProjectServerId = Pipeline.ProjectServerId,
                AiRunnerProfileId = Pipeline.AiRunnerProfileId
            });
            // The preparation is done and kept (nothing is undone); only the two variables are left to the user.
            if (!saved.IsSuccess)
                return string.Format(L["PipelineProdCompletionYamlNotSaved"], result.ComposeFile, result.Strategy,
                    saved.ErrorMessage ?? L["OperationFailed"].Value);
        }

        var pending = PipelineSetupProduction.PendingValues(result);
        _done = pending.Length > 0
            ? string.Format(L["PipelineProdCompletionDonePending"], result.ComposeFile, pending)
            : string.Format(L["PipelineProdCompletionDone"], result.ComposeFile);
        Toast.Success(_done);
        return null;
    }
}
