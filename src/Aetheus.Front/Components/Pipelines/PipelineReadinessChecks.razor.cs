// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>Readiness findings share localized resolution links across the wizard and project page.
/// Repository creation stays in a dialog; other creation pages receive <see cref="ReturnUrl"/> to
/// preserve navigation.</summary>
public partial class PipelineReadinessChecks
{
    [Parameter, EditorRequired] public IReadOnlyList<PipelineSetupReadinessCheckDto> Checks { get; set; } = [];
    [Parameter, EditorRequired] public int ProjectId { get; set; }

    /// <summary>Where the pages opened to fix a finding send the user back; validated as local.</summary>
    [Parameter] public string? ReturnUrl { get; set; }

    /// <summary>True, the default: a blocking finding is an alert. The setup summary sets it to false,
    /// where its single alert is the one saying the creation cannot go on.</summary>
    [Parameter] public bool BlockingAsAlert { get; set; } = true;

    /// <summary>Raised after a fix made over the page (a repository created), so the host checks again.</summary>
    [Parameter] public EventCallback OnChanged { get; set; }
}
