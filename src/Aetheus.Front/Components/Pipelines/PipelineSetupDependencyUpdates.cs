// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// PLAN-007 (recette R-9, user decision 2026-11-09): the "automatic dependency updates" tile of the wizard, ticked by default and
/// offered for an application and a package. Not for an external repository: its mirror is never written, so an update could not
/// be made there and the tile would promise nothing. Once the pipelines exist the project's setting is written from it: ticked
/// keeps the updates on (the default of every project), unticked turns them off. The wizard's address names the choice by what
/// leaves the default (<see cref="SkipOption"/>), so an address written before this option existed reopens ticked.
/// </summary>
internal sealed class PipelineSetupDependencyUpdates
{
    /// <summary>The option's name in the wizard's address, present only when the tile is unticked.</summary>
    public const string SkipOption = "skip-dependency-updates";

    public bool Enabled { get; set; } = true;

    public (string Name, Func<bool> Get, Action<bool> Set) Choice =>
        (SkipOption, () => !Enabled, skipped => Enabled = !skipped);

    public static bool IsOffered(PipelineArchitecture? architecture) =>
        architecture is PipelineArchitecture.Application or PipelineArchitecture.Package;

    public void AddAttributes(RenderTreeBuilder builder, object receiver, Action refresh, PipelineArchitecture? architecture)
    {
        builder.AddAttribute(246, nameof(PipelineSetupWizardStep.DependencyUpdates), Enabled);
        builder.AddAttribute(247, nameof(PipelineSetupWizardStep.DependencyUpdatesChanged),
            EventCallback.Factory.Create<bool>(receiver, value =>
            {
                Enabled = value;
                refresh();
            }));
        builder.AddAttribute(248, nameof(PipelineSetupWizardStep.DependencyUpdatesOffered), IsOffered(architecture));
    }

    /// <summary>Writes the project's setting when the tile was offered. Never blocks the wizard: a refusal only warns, the
    /// pipelines stay.</summary>
    public async Task ApplyAsync(AnalysisApi analysis, NotifyHelper toast, int projectId, PipelineArchitecture? architecture)
    {
        if (!IsOffered(architecture)) return;
        try
        {
            await analysis.SetDependencyUpdatesEnabledAsync(projectId, Enabled);
        }
        catch (HttpRequestException)
        {
            toast.Warning("DependencyUpdatesNotSaved", "DependencyUpdatesNotSavedDetail");
        }
    }
}
