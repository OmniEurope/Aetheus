// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.ServiceConnections;

/// <summary>The project names the service connections list's Project column filter offers, read
/// across every connection the caller can read.</summary>
public sealed record ServiceConnectionFilterValuesDto
{
    public List<string> Projects { get; init; } = [];
}
