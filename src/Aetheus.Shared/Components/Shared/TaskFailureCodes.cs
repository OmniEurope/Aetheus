// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Shared;

public static class TaskFailureCodes
{
    public const string InfrastructureMismatch = nameof(InfrastructureMismatch);
    public const string ToolError = nameof(ToolError);
    public const string BuildFailed = nameof(BuildFailed);
    public const string TestsFailed = nameof(TestsFailed);

    /// <summary>The target server's agent cannot execute the task: its protocol is unsupported or it
    /// does not declare the capability the operation needs. Raised by the backend, never by an agent,
    /// so the run fails with a reason instead of leaving the task Pending forever.</summary>
    public const string AgentIncompatible = nameof(AgentIncompatible);
}
