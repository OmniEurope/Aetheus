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
