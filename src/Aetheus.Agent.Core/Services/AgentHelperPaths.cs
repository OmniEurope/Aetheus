// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Services;

/// <summary>Expose the installer root-owned helper directory through <see cref="VariableName"/> so
/// tasks use the agent actual helper paths.</summary>
internal static class AgentHelperPaths
{
    internal const string VariableName = "AGENT_HELPERS_DIR";

    /// <summary>The Linux installer's directory (<c>install-agent-linux.sh</c>, AETHEUS_HELPER_DIR).
    /// There is no equivalent on Windows, where nothing is published.</summary>
    internal const string LinuxDirectory = "/usr/local/lib/aetheus";
}
