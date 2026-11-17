// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// R-26 (user decision 2026-11-05): the audience of the application (its visitors). A tile of the wizard's Supervision section,
/// offered for an application only. It starts ticked when the project has a web front (a Full-stack application serves its
/// interface, a front-end and back-end application holds a front-end) and unticked otherwise, until the user (or the address he
/// came back with) decides it himself. When the pipelines are created and the tile is ticked, the wizard prepares the audience
/// of the project's monitored application (<see cref="PipelineSetupCollection"/>); it never blocks the wizard.
/// The wizard's address names the choice by what leaves the default (<see cref="SkipOption"/>, written when the tile is unticked).
/// </summary>
internal sealed class PipelineSetupAudience
{
    /// <summary>The option's name in the wizard's address, present only when the tile is unticked.</summary>
    public const string SkipOption = "skip-audience";

    /// <summary>The capability name the architecture proposes it under (the same as the telemetry: an application).</summary>
    public const string Capability = PipelineSetupTelemetry.Capability;

    public bool Configure { get; set; } = true;

    /// <summary>True once the user (or the address he came back with) decided the tile himself: the project's shape no longer sets it.</summary>
    public bool Touched { get; set; }

    /// <summary>The choice as the wizard's address carries it: present when unticked, so absent means the default.</summary>
    public (string Name, Func<bool> Get, Action<bool> Set) Choice =>
        (SkipOption, () => !Configure, skipped => Configure = !skipped);

    /// <summary>True when the tile is offered: the type is an application.</summary>
    public static bool IsOffered(PipelineArchitecture? architecture) =>
        PipelineSetupArchitecture.Proposes(architecture, Capability);

    /// <summary>The application has a web front: the Full-stack tile serves the interface itself; the separate tile has one when its
    /// front-end part is chosen. False while the shape is not chosen.</summary>
    public static bool HasWebFront(PipelineArchitecture? architecture, bool? sameProject, bool frontend) =>
        architecture == PipelineArchitecture.Application && sameProject switch
        {
            true => true,
            false => frontend,
            null => false
        };

    /// <summary>Sets the tile from the project's shape until the user decided it: ticked with a web front. While the shape is not
    /// chosen it stays ticked, as the wizard resets it.</summary>
    public void ApplyDefault(PipelineArchitecture? architecture, bool? sameProject, bool frontend)
    {
        if (Touched || sameProject is null) return;
        Configure = HasWebFront(architecture, sameProject, frontend);
    }

    /// <summary>The step's parameters for this option: its value, its change (which marks it decided), and whether it is offered.</summary>
    public void AddAttributes(RenderTreeBuilder builder, object receiver, Action refresh, PipelineArchitecture? architecture)
    {
        builder.AddAttribute(243, nameof(PipelineSetupWizardStep.Audience), Configure);
        builder.AddAttribute(244, nameof(PipelineSetupWizardStep.AudienceChanged),
            EventCallback.Factory.Create<bool>(receiver, value =>
            {
                Configure = value;
                Touched = true;
                refresh();
            }));
        builder.AddAttribute(245, nameof(PipelineSetupWizardStep.AudienceOffered), IsOffered(architecture));
    }
}
