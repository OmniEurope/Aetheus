// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Git;

/// <summary> Brings the mirror of an external repository up to date, on demand. A pipeline whose
/// workspace comes from such a mirror asks for it just before its commit is pinned, so the run
/// builds what the remote holds now and not what the last scheduled fetch saw. It lives with the
/// repositories and the mirror mechanics (Git module), below the orchestrator that asks for it;
/// attaching and detaching an external repository stays the ExternalRepos module's work. </summary>
public interface IExternalMirrorRefresher
{
    /// <summary>
    /// Fetches the mirror behind <paramref name="slug"/> when that repository of the project is one.
    /// Returns null on success and for a repository that is not a mirror (nothing to fetch), else the
    /// reason the remote could not be read.
    /// </summary>
    Task<string?> RefreshAsync(int projectId, string slug, CancellationToken ct = default);
}
