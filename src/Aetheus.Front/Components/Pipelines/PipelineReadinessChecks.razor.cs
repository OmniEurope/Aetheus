// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Git;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>Readiness findings share localized resolution links across the wizard and project page.
/// Repository creation stays in a dialog; other creation pages receive <see cref="ReturnUrl"/> to
/// preserve navigation.</summary>
public partial class PipelineReadinessChecks
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<PipelineSetupReadinessCheckDto> Checks { get; set; } = [];
    [Parameter, EditorRequired] public int ProjectId { get; set; }

    /// <summary>Where the pages opened to fix a finding send the user back; validated as local.</summary>
    [Parameter] public string? ReturnUrl { get; set; }

    /// <summary>The wizard creates missing libraries and vaults itself: no link to them there.</summary>
    [Parameter] public bool ProvisionOffered { get; set; }

    /// <summary>Raised after a fix made over the page (a repository created), so the host checks again.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }

    private string? Href(PipelineSetupReadinessKind kind) =>
        ProvisionOffered && kind is PipelineSetupReadinessKind.MissingRequiredLibraries or PipelineSetupReadinessKind.MissingRequiredVaults
            ? null
            : PipelineReadinessText.RemediationHref(kind, ProjectId, ReturnUrl);

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
