// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Tests;

public sealed class AgentServiceRecoveryPolicyAuditTests
{
    [Fact]
    public void LinuxInstaller_RestartsAgentAfterWatchdogExit()
    {
        var script = ReadScript("install-agent-linux.sh");

        // The unit is the agent/aetheus-agent.service template the installer renders.
        Assert.Contains(
            "render_host_config agent/aetheus-agent.service \"$SYSTEMD_UNIT_PATH\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExecStart=#{EXEC_START}#\nRestart=always\nRestartSec=10",
            LinuxHostConfigTemplates.Read("agent/aetheus-agent.service"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsInstaller_RestartsAgentAfterWatchdogExit()
    {
        var script = ReadScript("install-agent-windows.ps1");

        Assert.Contains(
            "sc.exe failure $ServiceName reset= 60 actions= restart/10000/restart/10000/restart/10000",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsInstaller_PreservesIsolationParametersWhenSelfElevating()
    {
        var script = ReadScript("install-agent-windows.ps1");

        Assert.Contains("-InstallDir `\"$InstallDir`\"", script, StringComparison.Ordinal);
        Assert.Contains("-WorkDir `\"$WorkDir`\"", script, StringComparison.Ordinal);
        Assert.Contains("-ServiceName `\"$ServiceName`\"", script, StringComparison.Ordinal);
        Assert.Contains("if ($NonInteractive)     { $argList += \" -NonInteractive\" }", script, StringComparison.Ordinal);
    }

    private static string ReadScript(string name) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "deploy", "scripts", name));

    private static string FindRepoRoot() => Aetheus.Agent.Core.Tests.RepositoryScan.Root;
}
