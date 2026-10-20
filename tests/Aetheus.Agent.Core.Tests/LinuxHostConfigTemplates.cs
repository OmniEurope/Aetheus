// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Tests;

/// <summary>The installer renders versioned host-configuration templates under
/// deploy/agent-host-config using #{NAME}# placeholders.</summary>
internal static class LinuxHostConfigTemplates
{
    public static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "deploy", "agent-host-config", relative))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
}
