// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Git;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>One readiness finding. Repository creation stays in a dialog over the current page; the
/// other fixes are links that carry <see cref="ReturnUrl"/> to come back.</summary>
public partial class PipelineReadinessCheckBody
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineSetupReadinessCheckDto Check { get; set; } = default!;
    [Parameter, EditorRequired] public int ProjectId { get; set; }
    [Parameter] public string? ReturnUrl { get; set; }

    /// <summary>R-61: where the creation of an environment returns, when it is not <see cref="ReturnUrl"/>.</summary>
    [Parameter] public string? EnvironmentReturnUrl { get; set; }

    /// <summary>Raised after a fix made over the page (a repository created), so the host checks again.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    /// <summary>What a missing library or vault is for; null for any other item.</summary>
    private string? ItemPurpose(string item) => Check.Kind switch
    {
        PipelineSetupReadinessKind.MissingRequiredLibraries => PipelineReadinessText.Purpose(L, false, item),
        PipelineSetupReadinessKind.MissingRequiredVaults => PipelineReadinessText.Purpose(L, true, item),
        _ => null
    };

    private async Task CreateRepositoryAsync()
    {
        var created = await Dialog.OpenAsync<GitRepositoryCreateDialog>(
            L["NewRepository"].Value,
            new Dictionary<string, object?> { [nameof(GitRepositoryCreateDialog.ProjectId)] = ProjectId },
            new OmniDialogOptions { Width = "500px", AutoFocusFirstElement = false });
        if (created is true)
            await OnChanged.InvokeAsync();
    }
}
