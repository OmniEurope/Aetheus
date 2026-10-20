// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary> What the pipeline setup wizard builds and checks, asked before any detail. The type
/// decides which pipelines the next step proposes; <see cref="PipelineSetupArchitecture"/> holds
/// that mapping. </summary>
public enum PipelineArchitecture
{
    /// <summary>An application's server-hosted interface is an explicit option rather than a
    /// separate application type.</summary>
    Application,

    /// <summary>A package published to Aetheus' registry: nothing deployed, nothing rolled back.</summary>
    Package,

    /// <summary>An external application Aetheus deploys nothing of, where only the minimum is run.</summary>
    External
}
