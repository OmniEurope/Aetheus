// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Front.Components.Git;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The block "to do before creating" of the setup summary (R2-17, R2-26): the Git repository line,
/// the required libraries and vaults line (created in place, under a veil, without leaving the wizard), the
/// list of those libraries and vaults below both lines and, in a block of its own, the other findings. Readiness wording is localized through <see cref="PipelineReadinessChecks"/> while
/// the API supplies structured causes.</summary>
public partial class PipelineSetupReadinessPanel
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private PipelineSetupProvisionState ProvisionState { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    [Parameter] public PipelineSetupReadinessDto? Readiness { get; set; }
    [Parameter] public bool Loading { get; set; }

    /// <summary>Project the remediation links point at; without it a link would target no project.</summary>
    [Parameter, EditorRequired] public int ProjectId { get; set; }

    /// <summary>Templates the wizard is about to create, the same list the readiness check read.</summary>
    [Parameter] public IReadOnlyList<string> TemplateNames { get; set; } = [];

    /// <summary>The wizard's summary with its choices, where the pages opened from here come back to.</summary>
    [Parameter] public string? ReturnUrl { get; set; }

    /// <summary>True when the host's final action waits for the repository and the elements: the panel
    /// then says so, in its single alert, for as long as they are missing.</summary>
    [Parameter] public bool GateCreation { get; set; }

    /// <summary>Raised after the project changed from here (requirements provisioned, a repository
    /// created), so the host re-runs the readiness check.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    private bool _provisioning;
    private string? _provisionError;

    /// <summary>The veil lies over the block while it creates or the verdict is read again; the block
    /// stays mounted, the first reading (nothing to show yet) is the only loader.</summary>
    private bool Busy => Loading || _provisioning;

    private bool RepositoryMissing => PipelineSetupCreationGate.RepositoryMissing(Readiness);

    private IReadOnlyList<RecapElement> Pending => PipelineSetupCreationGate.MissingElements(Readiness);

    private int ActionCount => (RepositoryMissing ? 1 : 0) + (Pending.Count > 0 ? 1 : 0);

    private bool Blocked => GateCreation && PipelineSetupCreationGate.IsBlocked(Readiness);

    /// <summary>What is still to create, then what the summary created, each element once.</summary>
    private IReadOnlyList<RecapElement> Elements
    {
        get
        {
            var created = ProvisionState.For(ProjectId)
                .Select(element => new RecapElement(element.Kind == ProvisionedKind.Vault, element.Name, true))
                .ToList();
            var pending = Pending.Where(item => !created.Exists(done =>
                done.IsVault == item.IsVault && string.Equals(done.Name, item.Name, StringComparison.OrdinalIgnoreCase)));
            return [.. pending, .. created];
        }
    }

    /// <summary>Repository and element findings live in the block; the rest follows it. An empty
    /// repository is what a new one is: it is no finding of the summary.</summary>
    private IReadOnlyList<PipelineSetupReadinessCheckDto> OtherChecks =>
        Readiness is null
            ? []
            : [.. Readiness.Checks.Where(check => check.Kind is not (PipelineSetupReadinessKind.NoRepository
                or PipelineSetupReadinessKind.EmptyBranch
                or PipelineSetupReadinessKind.MissingRequiredLibraries
                or PipelineSetupReadinessKind.MissingRequiredVaults))];

    private bool CanProvision => Readiness is not null && TemplateNames.Count > 0 && Pending.Count > 0;

    private string StateText => ActionCount switch
    {
        0 => L["PipelineSetupRecapAllReady"].Value,
        1 => L["PipelineSetupRecapOneAction"].Value,
        var count => string.Format(L["PipelineSetupRecapActions"], count)
    };

    /// <summary>The state of the libraries and vaults line: what is still to create, else what was created.</summary>
    private string ElementsState => Pending.Count > 0
        ? string.Format(L["PipelineSetupRecapElementsToCreate"], Pending.Count)
        : string.Format(L["PipelineSetupRecapElementsCreated"], Elements.Count);

    private string RepositoryHelp => string.IsNullOrWhiteSpace(Readiness?.InspectedBranch)
        ? L["PipelineSetupRecapRepositoryHelp"].Value
        : $"{L["PipelineSetupRecapRepositoryHelp"].Value} {string.Format(L["PipelineSetupReadinessCheckedOn"], Readiness.InspectedBranch)}";

    private string ChipText(RecapElement element) => string.Join(" · ",
        element.Name,
        L[element.IsVault ? "PipelineSetupRecapKindVault" : "PipelineSetupRecapKindLibrary"].Value,
        L[element.Created ? "PipelineSetupRecapCreated" : "PipelineSetupRecapToCreate"].Value);

    private string Purpose(RecapElement element) => PipelineReadinessText.Purpose(L, element.IsVault, element.Name);

    private async Task CreateRepositoryAsync()
    {
        var created = await Dialog.OpenAsync<GitRepositoryCreateDialog>(
            L["NewRepository"].Value,
            new Dictionary<string, object?> { [nameof(GitRepositoryCreateDialog.ProjectId)] = ProjectId },
            new OmniDialogOptions { Width = "500px", AutoFocusFirstElement = false });
        if (created is true)
            await OnChanged.InvokeAsync();
    }

    private async Task ProvisionAsync()
    {
        _provisioning = true;
        _provisionError = null;
        try
        {
            var provisioned = await Api.Pipelines.ProvisionSetupRequirementsAsync(ProjectId, TemplateNames);
            if (provisioned is null)
            {
                _provisionError = L["PipelineSetupProvisionFailed"].Value;
                return;
            }

            ProvisionState.Record(ProjectId, provisioned);
            await OnChanged.InvokeAsync();
        }
        catch (HttpRequestException)
        {
            _provisionError = L["PipelineSetupProvisionFailed"].Value;
        }
        finally
        {
            _provisioning = false;
        }
    }
}
