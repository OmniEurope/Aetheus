// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

// Delegates server-scoped pipeline fetching and rendering to PipelinesList.
public partial class ServerPipelinesSection
{
    [Parameter, EditorRequired] public int ServerId { get; set; }
}
