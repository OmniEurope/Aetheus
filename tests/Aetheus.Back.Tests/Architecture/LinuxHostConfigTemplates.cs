// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Tests.Architecture;

/// <summary>Installer host configuration is rendered from versioned templates under
/// deploy/agent-host-config, using named substitution markers.</summary>
internal static class LinuxHostConfigTemplates
{
    public static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "deploy", "agent-host-config", relative))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    public static string Installer() =>
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "deploy", "scripts", "install-agent-linux.sh"));
}
