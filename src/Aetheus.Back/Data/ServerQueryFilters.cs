// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data;

/// <summary>Shares the retirement-filter name across servers and their environment and pool links
/// so authorized revival, retired-list, purge, watchdog and historical-detail queries can lift the
/// view consistently.</summary>
public static class ServerQueryFilters
{
    public const string ExcludeRetired = "ExcludeRetiredServers";
}
