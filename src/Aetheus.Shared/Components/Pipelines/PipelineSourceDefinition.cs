// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary> The <c>source:</c> block of a pipeline definition : the workspace is checked out from
/// another repository attached to the same project, while the definition itself, its templates and
/// its <c>.pipeline/configs</c> files keep coming from the pipeline's own repository. The typical
/// use is a pipeline kept in a private repository that builds and deploys what a public mirror
/// holds. The credentials of that mirror stay on the control plane: the runner clones the mirror
/// from Aetheus, pinned to the commit resolved when the run is prepared. </summary>
public sealed record PipelineSourceDefinition
{
    /// <summary>Slug of a repository attached to the pipeline's project (YAML key <c>repository:</c>).</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>Branch to check out; that repository's default branch when absent.</summary>
    public string? Branch { get; init; }

    /// <summary>
    /// When true the run is refused unless the tree checked out is, file for file, a tree the
    /// definition's repository holds: the one of the definition's own revision, or the head of
    /// <see cref="MatchBranch"/> when that is set (minus the paths <see cref="MatchExcludeFile"/> lists).
    /// It is the guard of a public copy: what is built is exactly what the private repository holds.
    /// </summary>
    public bool MustMatchDefinition { get; init; }

    /// <summary>
    /// A branch of the definition's repository whose head is the tree to match, instead of the
    /// definition's revision: the private record of an exported distribution, when the public copy is
    /// an adapted export rather than a subset of the private tree. Only read when
    /// <see cref="MustMatchDefinition"/> is set.
    /// </summary>
    public string? MatchBranch { get; init; }

    /// <summary>
    /// A file under <c>.pipeline/configs/</c>, read at the definition's revision: one glob per line
    /// (<c>#</c> starts a comment) naming the paths of the definition's repository that the other
    /// repository is not expected to contain. Only read when <see cref="MustMatchDefinition"/> is set.
    /// </summary>
    public string? MatchExcludeFile { get; init; }
}
