// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

// Delegates server-scoped release fetching and rendering to ReleasesList.
public partial class ServerReleasesSection
{
    [Parameter, EditorRequired] public int ServerId { get; set; }
}
