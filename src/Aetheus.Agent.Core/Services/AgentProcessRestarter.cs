// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Services;

/// <param name="exitJournal">Where the reason is written before the process ends.</param>
/// <param name="exit">Ends the process; the real <see cref="Environment.Exit(int)"/> unless a test replaces it.</param>
internal sealed class AgentProcessRestarter(
    IAgentExitJournal exitJournal,
    Action<int>? exit = null) : IAgentProcessRestarter
{
    private readonly Action<int> _exit = exit ?? Environment.Exit;

    public void Restart(string reason)
    {
        // Written before the exit, so the next process can tell the backend why this one ended.
        exitJournal.RecordExit(AgentExitKinds.Restart, reason);
        Console.Error.WriteLine(reason);
        _exit(1);
    }
}
