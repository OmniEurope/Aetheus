// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// R-41: the latest system release and installed packages an agent published of its host. One row per server,
/// replaced by each report; no row means the agent never reported (an older agent, a Windows host), never "nothing
/// installed". The organization is the server's own, read through it, so a server moved elsewhere carries its row.
/// </summary>
public class ServerHostInventory
{
    public int Id { get; set; }
    public int ServerId { get; set; }

    /// <summary>The <c>ID</c> of os-release, lower case (<c>ubuntu</c>).</summary>
    public string OsId { get; set; } = string.Empty;

    public string? OsVersionId { get; set; }
    public string? OsVersion { get; set; }
    public string? OsPrettyName { get; set; }

    /// <summary>The release of the kernel the host booted (<c>6.8.0-147-generic</c>); null from an agent that does not report
    /// it, in which case every installed kernel package is matched.</summary>
    public string? RunningKernelRelease { get; set; }

    /// <summary>The OSV ecosystem the packages are matched in (<c>Ubuntu:24.04:LTS</c>); null when no feed covers the system.</summary>
    public string? Ecosystem { get; set; }

    public int PackageCount { get; set; }

    /// <summary>The packages as reported (name, version, source), as JSON: read whole by the matching, never queried.</summary>
    public string PackagesJson { get; set; } = "[]";

    /// <summary>A hash of the release and the packages: a report that changes nothing is not matched again.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    public DateTime ReportedAt { get; set; }

    /// <summary>When the findings were last computed from this inventory; null while still to match.</summary>
    public DateTime? MatchedAt { get; set; }

    public Server Server { get; set; } = null!;
}
