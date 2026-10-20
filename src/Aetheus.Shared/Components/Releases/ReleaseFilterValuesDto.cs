// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Releases;

/// <summary> The values the releases list's checkable Project and Pipeline filters offer, across
/// every release of the caller's scope (the list is loaded page by page). </summary>
public sealed record ReleaseFilterValuesDto
{
    public List<string> ProjectNames { get; init; } = [];
    public List<string> SourcePipelineNames { get; init; } = [];
}
