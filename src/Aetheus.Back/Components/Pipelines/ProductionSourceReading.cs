// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Git;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Recette R-69: the repository the production analysis reads for a project, and whether that project's source is an
/// external Git repository. An external repository is read through its internal mirror like any repository; the two states in
/// which it cannot be are named instead of being reported as a generic "nothing to read".</summary>
/// <param name="Repository">The project's own repository holding at least one commit; null when there is none.</param>
/// <param name="ExternalSource">The project's source is an external repository, or should be (attached beside an internal
/// repository that holds no commit).</param>
/// <param name="Problem">Why <paramref name="Repository"/> is null for an external source; none otherwise.</param>
internal sealed record ProductionSourceReading(
    GitLightRepoDto? Repository, bool ExternalSource, ProductionSourceProblem Problem)
{
    public static ProductionSourceReading Of(IEnumerable<GitLightRepoDto> repositories)
    {
        var source = ProjectSourceRepositories.Of(repositories);
        var problem = source.Readable is not null ? ProductionSourceProblem.None
            : source.ExternalBesideEmpty ? ProductionSourceProblem.ExternalNotTheSource
            : source.OwnIsExternal ? ProductionSourceProblem.ExternalNotSynchronized
            : ProductionSourceProblem.None;
        return new ProductionSourceReading(source.Readable, source.ExternalSource, problem);
    }
}
