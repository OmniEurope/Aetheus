// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Servers;

/// <summary> The values the servers list's checkable column filters offer. The list is loaded page
/// by page, so the values present across every server the caller can read come from the API rather
/// than from the rows on screen. </summary>
public sealed record ServerFilterValuesDto
{
    public List<string> OsDescriptions { get; init; } = [];
    public List<string> AgentVersions { get; init; } = [];
    public List<string> Tags { get; init; } = [];
}

/// <summary> The servers list's "agent version" filter is one checkable list mixing versions and
/// compatibility states. A state travels as this prefix and the member name, a version as itself.
/// </summary>
public static class ServerAgentFilter
{
    public const string StatePrefix = "state:";

    public static string State(AgentCompatibilityStatus status) => StatePrefix + status;
}
