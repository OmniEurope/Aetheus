// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The "start from" choice of the Project step: new pipelines from the templates, or the
/// pipelines of an existing project.</summary>
public partial class PipelineSetupStartFrom
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>True when the pipelines of an existing project are the starting point.</summary>
    [Parameter] public bool CopyMode { get; set; }
    [Parameter] public EventCallback<bool> CopyModeChanged { get; set; }

    private string Templates => L["PipelineSetupStartTemplates"].Value;
    private string ExistingProject => L["PipelineSetupStartExistingProject"].Value;

    private IReadOnlyList<OmniOption<string>> Options =>
    [
        new OmniOption<string>(Templates, Templates),
        new OmniOption<string>(ExistingProject, ExistingProject)
    ];

    private string Selected => CopyMode ? ExistingProject : Templates;

    private Task OnChanged(string label) => CopyModeChanged.InvokeAsync(label == ExistingProject);
}
