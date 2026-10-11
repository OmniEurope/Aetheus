// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R1-18 / R1-31: what the pipeline setup wizard builds and checks, asked before any detail.
/// The type decides which pipelines the next step proposes; <see cref="PipelineSetupArchitecture"/>
/// holds that mapping.
/// </summary>
public enum PipelineArchitecture
{
    /// <summary>An application built and delivered by the pipelines. Whether its interface is served by
    /// the server project is a yes/no question, not a type of its own (recette R1-31 decision).</summary>
    Application,

    /// <summary>A package published to Aetheus' registry: nothing deployed, nothing rolled back.</summary>
    Package,

    /// <summary>An external application Aetheus deploys nothing of, where only the minimum is run.</summary>
    External
}
