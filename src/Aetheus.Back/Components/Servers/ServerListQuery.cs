// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Servers;

/// <summary> The column filters of the servers list. The stored columns go through the generic map;
/// the tags (kept as JSON) and the agent column, whose list mixes versions with a compatibility
/// state computed by the policy, are resolved to server ids beforehand from facts read in memory.
/// </summary>
internal static class ServerListQuery
{
    internal const string TagsKey = "Tags";
    internal const string AgentVersionKey = "AgentVersion";

    internal static readonly GridQueryMap<Server> Columns = new GridQueryMap<Server>()
        .Text("name", s => s.Name)
        .Text("hostname", s => s.Hostname)
        .Enum("type", s => s.Type)
        .Enum("status", s => s.Status)
        .Text("osDescription", s => s.OsDescription)
        .Date("lastHeartbeat", s => s.LastHeartbeat);

    /// <summary>Whether the filter is one this class resolves itself rather than through <see cref="Columns"/>.</summary>
    internal static bool IsResolvedInMemory(GridFilter filter) =>
        string.Equals(filter.Field, TagsKey, StringComparison.OrdinalIgnoreCase)
        || string.Equals(filter.Field, AgentVersionKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The ids of the servers whose tags hold any of the ticked ones. Only a checkable list is accepted
    /// on this column, the only filter the grid offers it.
    /// </summary>
    internal static HashSet<int> TaggedWithAny(GridFilter filter, IEnumerable<(int Id, IReadOnlyCollection<string> Tags)> servers)
    {
        var wanted = ListOf(filter).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. servers.Where(server => server.Tags.Any(wanted.Contains)).Select(server => server.Id)];
    }

    /// <summary>
    /// The ids of the servers matching the agent column's ticked items: a server matches when its version
    /// is ticked or its compatibility state is (one list, so any ticked item is enough).
    /// </summary>
    internal static HashSet<int> AgentMatchingAny(
        GridFilter filter,
        IEnumerable<(int Id, string Version, AgentCompatibilityStatus? State)> servers)
    {
        var items = ListOf(filter);
        var versions = items.Where(item => !item.StartsWith(ServerAgentFilter.StatePrefix, StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var states = items.Where(item => item.StartsWith(ServerAgentFilter.StatePrefix, StringComparison.Ordinal))
            .Select(item => Enum.TryParse<AgentCompatibilityStatus>(item[ServerAgentFilter.StatePrefix.Length..], true, out var state)
                ? state
                : throw new BadRequestException($"'{item}' is not an agent state."))
            .ToHashSet();
        return [.. servers
            .Where(server => versions.Contains(server.Version) || server.State is { } state && states.Contains(state))
            .Select(server => server.Id)];
    }

    private static IReadOnlyList<string> ListOf(GridFilter filter) => filter.Operator switch
    {
        GridFilterOperator.In => GridQueryMap<Server>.SplitList(filter.Value),
        GridFilterOperator.Equals when !string.IsNullOrWhiteSpace(filter.Value) => [filter.Value],
        _ => throw new BadRequestException($"'{filter.Field}' only accepts a list of values.")
    };
}

/// <summary>What the servers list's checkable filters read of one server.</summary>
public sealed record ServerFilterFact(int Id, string OsDescription, string AgentVersion, List<string> Tags);
