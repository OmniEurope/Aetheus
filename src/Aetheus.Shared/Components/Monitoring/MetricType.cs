// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Monitoring;

public enum MetricType
{
    Cpu = 0,
    Memory = 1,
    Disk = 2,
    DiskFree = 3,
    BuildCache = 4,
    /// <summary>Fullest inode table of the server, in percent (PLAN-010 lot 1).</summary>
    Inodes = 5,
    /// <summary>
    /// Agent clock offset against the backend's, in seconds, as the measure guarantees it (the
    /// absolute offset less its uncertainty). PLAN-010 lot 1.
    /// </summary>
    ClockOffset = 6
}
