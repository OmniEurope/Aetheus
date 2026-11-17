// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.VariableLibraries;

/// <summary>
/// The servers a library's ports can be allocated or checked on. Both port dialogs open on this read
/// and treat a refused or failed one the same way: nothing to choose from, and a "load failed" message.
/// </summary>
internal static class LibraryPortTargets
{
    /// <summary>
    /// The server the dialogs start on: the one the API preselected, else the only one reachable (R-88).
    /// A lone option in a select looks chosen, so leaving the value null would show a selection nobody
    /// made and keep the button disabled. With several servers there is nothing to guess: null.
    /// </summary>
    public static int? InitialServerId(PortAllocationTargetsDto targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return targets.PreselectedServerId ?? (targets.Servers.Count == 1 ? targets.Servers[0].ServerId : null);
    }

    /// <summary>The port a <c>PORT_*</c> entry holds, when it holds a valid one (what the check matches conflicts on).</summary>
    public static bool TryReadPort(VariableEntryDto? entry, out int port)
    {
        port = 0;
        return entry is not null
            && entry.Key.StartsWith(PortRegistryLimits.PortKeyPrefix, StringComparison.Ordinal)
            && int.TryParse(entry.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
            && port is > 0 and <= 65535;
    }

    /// <summary>One key's port on a server, in one request (the endpoint creates a key the library lacks).
    /// The entry written, or null with the reason to show: the API's own, else <paramref name="fallbackError"/>.</summary>
    public static async Task<(VariableEntryDto? Written, string? Error)> TryAllocateAsync(
        ApiClient api, int libraryId, int serverId, string key, string fallbackError)
    {
        try
        {
            var outcome = await api.Variables.AllocateLibraryPortsAsync(
                libraryId, new AllocateLibraryPortsRequest { ServerId = serverId, Keys = [key] });
            if (outcome.Value is { Count: > 0 } written) return (written[0], null);
            return (null, string.IsNullOrWhiteSpace(outcome.Error?.Message) ? fallbackError : outcome.Error.Message);
        }
        catch (HttpRequestException)
        {
            return (null, fallbackError);
        }
    }

    /// <summary>The first free port, reserved by nobody, for the add row to show (the write of the entry reserves it).
    /// The port, or null with the reason: a library that cannot allocate (blocker) and several servers with none
    /// preselected both send the operator to the allocation dialog.</summary>
    public static async Task<(int? Port, string? Error)> TryPreviewFreeAsync(
        ApiClient api, int libraryId, string key, string fallbackError, string useAllocateError)
    {
        var targets = await TryLoadAsync(api, libraryId);
        if (targets is null) return (null, fallbackError);
        if (targets.Blocker != PortAllocationBlocker.None || InitialServerId(targets) is not { } serverId)
            return (null, useAllocateError);

        try
        {
            var outcome = await api.Variables.PreviewFreeLibraryPortsAsync(
                libraryId, new AllocateLibraryPortsRequest { ServerId = serverId, Keys = [key] });
            if (outcome.Value is [> 0 and var port]) return (port, null);
            return (null, string.IsNullOrWhiteSpace(outcome.Error?.Message) ? fallbackError : outcome.Error.Message);
        }
        catch (HttpRequestException)
        {
            return (null, fallbackError);
        }
    }

    /// <summary>The library's <c>PORT_</c> entries, or null when they could not be read.</summary>
    public static async Task<List<VariableEntryDto>?> TryReadPortEntriesAsync(ApiClient api, int libraryId)
    {
        try
        {
            var page = await api.Variables.GetVariableEntriesPageAsync(
                libraryId, page: 1, pageSize: 200, search: PortRegistryLimits.PortKeyPrefix);
            return [.. page.Items.Where(entry =>
                entry.Key.StartsWith(PortRegistryLimits.PortKeyPrefix, StringComparison.Ordinal))];
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Null when the API returned nothing or the request failed.</summary>
    public static async Task<PortAllocationTargetsDto?> TryLoadAsync(ApiClient api, int libraryId)
    {
        try
        {
            return await api.Variables.GetPortAllocationTargetsAsync(libraryId);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
