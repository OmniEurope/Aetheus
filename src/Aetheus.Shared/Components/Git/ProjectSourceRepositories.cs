// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Git;

/// <summary>Recette R-69: which of a project's repositories is its source, and whether that source is an external Git
/// repository read through its internal mirror. One reading shared by the production analysis (back) and the project overview
/// (front), so both agree on the state an empty internal repository leaves next to an external one.</summary>
/// <param name="Own">The project's own repositories: never its configuration repository, never an external source attached
/// beside it.</param>
/// <param name="Readable">The own repository holding at least one commit; null when there is none.</param>
/// <param name="OwnIsExternal">The project's source is the mirror of an external repository.</param>
/// <param name="ExternalBesideEmpty">An external repository is attached beside an internal one that holds no commit, which stays
/// the project's source: the state a wizard that created an empty repository leaves behind.</param>
public sealed record ProjectSourceRepositories(
    IReadOnlyList<GitLightRepoDto> Own, GitLightRepoDto? Readable, bool OwnIsExternal, bool ExternalBesideEmpty)
{
    /// <summary>The project's source is an external repository, or should be.</summary>
    public bool ExternalSource => OwnIsExternal || ExternalBesideEmpty;

    public static ProjectSourceRepositories Of(IEnumerable<GitLightRepoDto> repositories)
    {
        var all = repositories.ToList();
        var own = all.Where(item => !item.IsAdditionalSource && !item.IsConfiguration).ToList();
        var ownIsExternal = own.Exists(item => item.IsExternalMirror);
        var besideEmpty = !ownIsExternal
            && own.TrueForAll(item => item.IsEmpty)
            && all.Exists(item => item is { IsAdditionalSource: true, IsExternalMirror: true });
        return new ProjectSourceRepositories(own, own.Find(item => !item.IsEmpty), ownIsExternal, besideEmpty);
    }
}
