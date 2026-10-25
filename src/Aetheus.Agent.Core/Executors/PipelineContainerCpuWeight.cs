// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Executors;

/// <summary>
/// R-6: on the production VPS the pipeline runner and the Aetheus API and PostgreSQL containers share one
/// machine, and builds made the API slow. Every container the agent starts for pipeline work (step and
/// scanner containers) gets this low <c>docker run --cpu-shares</c> weight against the Docker default of 1024,
/// so under contention the production containers win 4 to 1. The weight is relative, not a cap: it only
/// matters under contention, and an idle machine still gives builds all the CPU.
/// </summary>
internal static class PipelineContainerCpuWeight
{
    public const string Shares = "256";
}
