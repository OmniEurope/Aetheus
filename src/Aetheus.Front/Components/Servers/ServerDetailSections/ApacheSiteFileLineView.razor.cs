// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Servers.ServerDetailSections;

public partial class ApacheSiteFileLineView
{
    /// <summary>The site file's name.</summary>
    [Parameter, EditorRequired] public string FileName { get; set; } = string.Empty;

    /// <summary>The ports its blocks serve ("80, 443").</summary>
    [Parameter, EditorRequired] public string Ports { get; set; } = string.Empty;

    /// <summary>The account that owns the file when it is not the agent's own, so Aetheus cannot delete it; null or
    /// blank shows no badge.</summary>
    [Parameter] public string? Owner { get; set; }
}
