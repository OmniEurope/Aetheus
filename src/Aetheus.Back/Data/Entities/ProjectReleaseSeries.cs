// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Data.Entities;

/// <summary>
/// The release counter of one version series of a project. A series is the <c>major.minor</c> of the
/// code's <c>VersionPrefix</c> (<c>1.4</c>, or the empty series for code that declares none); its
/// release number restarts at 0 and counts up from there, whatever the other series reached.
/// </summary>
public class ProjectReleaseSeries
{
    public int Id { get; set; }
    public int ProjectId { get; set; }

    /// <summary><c>major.minor</c>, or empty for code that declares no <c>VersionPrefix</c>.</summary>
    public string Series { get; set; } = string.Empty;

    /// <summary>The release number the series' next release takes, or the first free number above it
    /// when that one is already used (an administrator may set it back below numbers already
    /// handed out). 0 for a series nothing has released in yet.</summary>
    public int NextNumber { get; set; }

    public Project Project { get; set; } = null!;
}
