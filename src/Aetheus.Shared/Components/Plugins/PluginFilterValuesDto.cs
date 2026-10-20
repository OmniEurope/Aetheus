// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Plugins;

/// <summary>The authors the plugins list's Author column filter offers, across every
/// plugin.</summary>
public sealed record PluginFilterValuesDto
{
    public List<string> Authors { get; init; } = [];
}
