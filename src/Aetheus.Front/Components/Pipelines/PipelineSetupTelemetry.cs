// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// R-47 (user decision 2026-11-03): the telemetry of the application. A tile of the wizard, ticked by default, offered for an
/// application only (a package or an external repository has no application to observe). When the pipelines are created and the
/// tile is ticked, the wizard configures the telemetry through the project's monitored application, the path the deployment
/// already reads to inject the ingest key and the OTLP endpoints (<c>AppDeployEnvProvider</c>); it never blocks the wizard.
/// R-26: the preparation itself, with the audience, is <see cref="PipelineSetupCollection"/>.
/// What the application's repository still lacks (the <c>Aetheus.Telemetry</c> NuGet package) is told on the project's overview
/// for as long as the stored repository inventory does not list it.
/// The wizard's address names the choice by what leaves the default (<see cref="SkipOption"/>, written when the tile is
/// unticked), so an address written before this option existed reopens ticked.
/// </summary>
internal sealed class PipelineSetupTelemetry
{
    /// <summary>The option's name in the wizard's address, present only when the tile is unticked.</summary>
    public const string SkipOption = "skip-telemetry";

    /// <summary>The capability name the architecture proposes it under.</summary>
    public const string Capability = "telemetry";

    public bool Configure { get; set; } = true;

    /// <summary>The choice as the wizard's address carries it: present when unticked, so absent means the default.</summary>
    public (string Name, Func<bool> Get, Action<bool> Set) Choice =>
        (SkipOption, () => !Configure, skipped => Configure = !skipped);

    /// <summary>True when the tile is offered: the type is an application.</summary>
    public static bool IsOffered(PipelineArchitecture? architecture) =>
        PipelineSetupArchitecture.Proposes(architecture, Capability);

    /// <summary>The step's parameters for this option: its value, its change, and whether it is offered.</summary>
    public void AddAttributes(RenderTreeBuilder builder, object receiver, Action refresh, PipelineArchitecture? architecture)
    {
        builder.AddAttribute(240, nameof(PipelineSetupWizardStep.Telemetry), Configure);
        builder.AddAttribute(241, nameof(PipelineSetupWizardStep.TelemetryChanged),
            EventCallback.Factory.Create<bool>(receiver, value =>
            {
                Configure = value;
                refresh();
            }));
        builder.AddAttribute(242, nameof(PipelineSetupWizardStep.TelemetryOffered), IsOffered(architecture));
    }
}
