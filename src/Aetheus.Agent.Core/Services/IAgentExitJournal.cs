// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Services;

/// <summary>
/// Remembers, across agent processes, how the agent ended, so the backend can say why a restart happened
/// instead of only that the session id changed.
/// </summary>
public interface IAgentExitJournal
{
    /// <summary>
    /// How the previous process ended, until <see cref="MarkReported"/> confirms the backend received it.
    /// Null on a first install, and once reported.
    /// </summary>
    AgentExitReportDto? GetPendingReport();

    /// <summary>The backend accepted the report: forget it so it is sent once.</summary>
    void MarkReported();

    /// <summary>
    /// Records that this process is ending, atomically. Only the first call of a process counts: the host
    /// stop that follows a deliberate restart must not overwrite its reason.
    /// </summary>
    void RecordExit(string kind, string reason);
}
