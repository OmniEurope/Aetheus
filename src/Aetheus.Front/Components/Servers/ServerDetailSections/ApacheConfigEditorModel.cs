// SPDX-License-Identifier: EUPL-1.2
using System;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Shared model bridging <see cref="ServerApacheSection"/> (the SignalR receiver) and
/// <see cref="ApacheConfigEditorDialog"/> (the OmniDialogService-hosted form). The vhost config
/// text is fetched on the agent and pushed back over SignalR into the section's
/// <c>HandleTaskCompleted</c>; the section writes it here and raises <see cref="OnChanged"/>
/// so the open dialog can refresh. Public (not internal) because it is bound to a public
/// dialog <c>[Parameter]</c>.
/// </summary>
public sealed class ApacheConfigEditorModel
{
    public string SiteName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Loading { get; set; }

    /// <summary>Recette R3-30: why the config could not be read (the failed task and its output), shown
    /// instead of an empty editor; null when the read succeeded.</summary>
    public string? Error { get; set; }

    /// <summary>Raised when the section pushes new content/loading state from SignalR.</summary>
    public event Action? OnChanged;

    public void NotifyChanged() => OnChanged?.Invoke();
}
