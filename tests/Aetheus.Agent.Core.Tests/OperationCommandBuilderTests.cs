// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The argv handed to sudo, systemctl and sc is the security boundary of each operation: one entry per
/// sudoers rule, no shell. These tests pin the exact command each operation kind resolves to, and that a kind
/// the executor does not own resolves to nothing instead of to a command.</summary>
public class OperationCommandBuilderTests
{
    private static string[] Args(ProcessStartInfo psi) => [.. psi.ArgumentList];

    [Theory]
    [InlineData(OperationKind.PortsentryStart, "start")]
    [InlineData(OperationKind.PortsentryStop, "stop")]
    [InlineData(OperationKind.PortsentryRestart, "restart")]
    public void PortsentryBuildPsi_LifecycleKinds_RunSystemctlThroughNonInteractiveSudo(OperationKind kind, string verb)
    {
        var psi = PortsentryOperationExecutor.BuildPsi(kind);

        Assert.NotNull(psi);
        Assert.Equal("sudo", psi.FileName);
        Assert.Equal(["-n", "/bin/systemctl", verb, "portsentry.service"], Args(psi));
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void PortsentryBuildPsi_Status_ReadsTheUnitStateWithoutSudo()
    {
        var psi = PortsentryOperationExecutor.BuildPsi(OperationKind.PortsentryStatus);

        Assert.NotNull(psi);
        Assert.Equal("systemctl", psi.FileName);
        Assert.Equal(["is-active", "portsentry.service"], Args(psi));
        Assert.True(psi.RedirectStandardOutput);
    }

    [Fact]
    public void PortsentryBuildPsi_AKindOfAnotherExecutor_ResolvesToNoCommand() =>
        Assert.Null(PortsentryOperationExecutor.BuildPsi(OperationKind.ApacheReload));

    [Theory]
    [InlineData(OperationKind.ApacheReload, new[] { "-n", "/bin/systemctl", "reload", "apache2.service" })]
    [InlineData(OperationKind.ApacheStart, new[] { "-n", "/bin/systemctl", "start", "apache2.service" })]
    [InlineData(OperationKind.ApacheStop, new[] { "-n", "/bin/systemctl", "stop", "apache2.service" })]
    [InlineData(OperationKind.ApacheRestart, new[] { "-n", "/bin/systemctl", "restart", "apache2.service" })]
    [InlineData(OperationKind.ApacheTestConfig, new[] { "-n", "/usr/local/lib/aetheus/aetheus-apache-configtest" })]
    public void ApacheBuildPsi_EachKind_ResolvesToItsAllowListedSudoCommand(OperationKind kind, string[] expected)
    {
        var psi = ApacheOperationExecutor.BuildPsi(kind);

        Assert.NotNull(psi);
        Assert.Equal("sudo", psi.FileName);
        Assert.Equal(expected, Args(psi));
    }

    [Fact]
    public void ApacheBuildPsi_AKindOfAnotherExecutor_ResolvesToNoCommand() =>
        Assert.Null(ApacheOperationExecutor.BuildPsi(OperationKind.PortsentryStart));

    [Theory]
    [InlineData(OperationKind.ServiceStart, "start")]
    [InlineData(OperationKind.ServiceStop, "stop")]
    [InlineData(OperationKind.ServiceStatus, "query")]
    public void ServiceBuildWindowsArgs_SingleVerbKinds_RunScWithTheServiceNameAsOneArgument(OperationKind kind, string verb)
    {
        var psi = ServiceOperationExecutor.BuildWindowsArgs(kind, "My Service; calc");

        Assert.NotNull(psi);
        Assert.Equal("sc", psi.FileName);
        Assert.Equal([verb, "My Service; calc"], Args(psi));
    }

    [Fact]
    public void ServiceBuildWindowsArgs_Restart_IsNotASingleCommand() =>
        Assert.Null(ServiceOperationExecutor.BuildWindowsArgs(OperationKind.ServiceRestart, "spooler"));

    [Fact]
    public void SelfUpdateLinuxLauncher_RunsTheScriptThenItsArgumentsUnderShell()
    {
        var psi = AgentSelfUpdateLauncher.BuildLinuxUpdaterProcess("/opt/agent/update.sh", ["--to", "1.2.3"]);

        var args = Args(psi);
        if (psi.FileName == "sh")
        {
            // No setsid on this machine: the shell runs the script directly.
            Assert.Equal(["/opt/agent/update.sh", "--to", "1.2.3"], args);
        }
        else
        {
            Assert.EndsWith("setsid", psi.FileName, StringComparison.Ordinal);
            Assert.Equal(["sh", "/opt/agent/update.sh", "--to", "1.2.3"], args);
        }
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
    }

    [Fact]
    public void SelfUpdateLinuxLauncher_WithoutArguments_PassesOnlyTheScript()
    {
        var psi = AgentSelfUpdateLauncher.BuildLinuxUpdaterProcess("/opt/agent/update.sh", null);

        Assert.Equal("/opt/agent/update.sh", Args(psi)[^1]);
        Assert.DoesNotContain("--to", Args(psi));
    }
}
