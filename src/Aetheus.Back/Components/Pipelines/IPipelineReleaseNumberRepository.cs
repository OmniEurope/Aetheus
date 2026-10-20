// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>The release number a run was created with and the version series it counts in.</summary>
public sealed record ReleaseNumberAssignment(string? Series, int Number);

/// <summary>
/// The per-project, per-series release counter behind <c>BUILD_RELEASENUMBER</c>: the next number a
/// series hands out, the last one it handed out, and the one a run was created with. The port of
/// <see cref="PipelineReleaseNumbers"/> (audit AUD-07). <see cref="IPipelineRepository"/> composes it
/// and delegates to the same queries. A series is the <c>major.minor</c> of the released commit
/// (<see cref="ReleaseSeries"/>); every series counts from 0 on its own.
/// </summary>
public interface IPipelineReleaseNumberRepository
{
    /// <summary>Takes the next release number of the project's series, atomically; null when the
    /// project is gone.</summary>
    Task<int?> AllocateNextReleaseNumberAsync(int projectId, string series, CancellationToken ct = default);

    /// <summary>The last release number the series handed out, 0 before its first; null when the
    /// project is gone.</summary>
    Task<int?> GetLatestReleaseNumberAsync(int projectId, string series, CancellationToken ct = default);

    /// <summary>The release number (and series) a run was created with, null when it has none.</summary>
    Task<ReleaseNumberAssignment?> GetRunReleaseAsync(int runId, CancellationToken ct = default);
}
