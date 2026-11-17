// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R-13 (user decision 2026-11-09): the "expand/contract model" tile of the wizard, offered for an application that has a
/// database and unticked by default. Ticked, it turns on the project's migration gate once the pipelines exist (the project's
/// <c>MigrationGateEnabled</c> setting, which the project's settings change later too), and the project's overview then shows the
/// expand/contract tracking tab. Unticked, it writes nothing: it never turns a setting off that the project's settings turned on.
/// The wizard's address names the option only when it is ticked.
/// </summary>
internal sealed class PipelineSetupMigrationGate
{
    /// <summary>The option's name in the wizard's address, present only when the tile is ticked.</summary>
    public const string Option = "expand-contract";

    public bool Enabled { get; set; }

    public (string Name, Func<bool> Get, Action<bool> Set) Choice => (Option, () => Enabled, value => Enabled = value);

    /// <summary>Only an application that declares a database has migrations to gate; a package and an external repository have none.</summary>
    public static bool IsOffered(PipelineArchitecture? architecture, bool database) =>
        architecture == PipelineArchitecture.Application && database;

    public void AddAttributes(RenderTreeBuilder builder, object receiver, Action refresh, PipelineArchitecture? architecture, bool database)
    {
        builder.AddAttribute(260, nameof(PipelineSetupWizardStep.ExpandContract), Enabled);
        builder.AddAttribute(261, nameof(PipelineSetupWizardStep.ExpandContractChanged),
            EventCallback.Factory.Create<bool>(receiver, value =>
            {
                Enabled = value;
                refresh();
            }));
        builder.AddAttribute(262, nameof(PipelineSetupWizardStep.ExpandContractOffered), IsOffered(architecture, database));
    }

    /// <summary>Turns the project's migration gate on when the tile was offered and ticked. Never blocks the wizard: a refusal only
    /// warns, the pipelines stay.</summary>
    public async Task ApplyAsync(ProjectsApi projects, NotifyHelper toast, int projectId, PipelineArchitecture? architecture, bool database)
    {
        if (!Enabled || !IsOffered(architecture, database)) return;
        try
        {
            if (!(await projects.SetMigrationGateEnabledAsync(projectId, true)).Success)
                toast.Warning("MigrationGateNotSaved", "MigrationGateNotSavedDetail");
        }
        catch (HttpRequestException)
        {
            toast.Warning("MigrationGateNotSaved", "MigrationGateNotSavedDetail");
        }
    }
}
