// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Shared;

public static class TaskFailureCodes
{
    public const string InfrastructureMismatch = nameof(InfrastructureMismatch);
    public const string ToolError = nameof(ToolError);
    public const string BuildFailed = nameof(BuildFailed);
    public const string TestsFailed = nameof(TestsFailed);

    /// <summary>R-68: a value the step needs is missing or empty (a variable of its environment sample). It is a
    /// configuration to complete in a library, a vault or an environment, never a problem of the server.</summary>
    public const string ConfigurationIncomplete = nameof(ConfigurationIncomplete);

    /// <summary>The target server's agent cannot execute the task: its protocol is unsupported or it
    /// does not declare the capability the operation needs. Raised by the backend, never by an agent,
    /// so the run fails with a reason instead of leaving the task Pending forever.</summary>
    public const string AgentIncompatible = nameof(AgentIncompatible);

    /// <summary>The agent session that held the task was replaced (agent restart, host reboot) before
    /// the task could finish, and the task is not replayed on its own. Raised by the backend.</summary>
    public const string AgentSessionReplaced = nameof(AgentSessionReplaced);
}
