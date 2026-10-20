// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The environment a stage names, as the run's project sees it, and the checks it carries. The port of
/// the environment lock gate and the environment-check guard (audit AUD-07).
/// <see cref="IPipelineRepository"/> composes it and delegates to the same queries.
/// </summary>
public interface IPipelineEnvironmentReader
{
    /// <summary>BACK-018: the environment called <paramref name="name"/> as a run of
    /// <paramref name="projectId"/> sees it: the project's own first, then a shared one (no project),
    /// never one of another project. Loaded with its servers and checks.</summary>
    Task<Data.Entities.Environment?> FindEnvironmentByNameAsync(string name, int? projectId, CancellationToken ct = default);

    Task<List<EnvironmentCheck>> GetEnvironmentChecksAsync(int environmentId, CancellationToken ct = default);
}
