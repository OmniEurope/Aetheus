// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

public class FirewallOperationExecutorTests
{
    private static readonly Func<string, TaskLogLevel, Task> NoOutput = (_, _) => Task.CompletedTask;

    private static FirewallOperationExecutor Build()
        => new(NullLogger<FirewallOperationExecutor>.Instance);

    [Fact]
    public void CanHandle_OnlyFirewallKinds()
    {
        var exec = Build();
        Assert.True(exec.CanHandle(OperationKind.FirewallAllow));
        Assert.True(exec.CanHandle(OperationKind.FirewallDeny));
        Assert.True(exec.CanHandle(OperationKind.FirewallDeleteRule));
        Assert.True(exec.CanHandle(OperationKind.FirewallSetEnabled));
        Assert.False(exec.CanHandle(OperationKind.SystemPackageUpgrade));
    }

    [Fact]
    public async Task InvalidPort_RejectedBeforeAnyPrivilegedCall()
    {
        // Port out of range fails the shared target validator - refused regardless of OS, no sudo attempted.
        var result = await Build().ExecuteAsync(OperationKind.FirewallAllow, "99999", 60, NoOutput, TestContext.Current.CancellationToken);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task InvalidToggleTarget_Rejected()
    {
        var result = await Build().ExecuteAsync(OperationKind.FirewallSetEnabled, "nonsense", 60, NoOutput, TestContext.Current.CancellationToken);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public void HelperPath_IsTheRootOwnedHelper()
        => Assert.Equal("/usr/local/lib/aetheus/aetheus-firewall", FirewallOperationExecutor.HelperPath);

    [Theory]
    [InlineData("enable")]
    [InlineData("disable")]
    public void ResolveHelperCommand_Toggle_PassesTheValidatedTarget(string target) =>
        Assert.Equal(["-n", FirewallOperationExecutor.HelperPath, target],
            FirewallOperationExecutor.ResolveHelperCommand(OperationKind.FirewallSetEnabled, target, new Dictionary<string, string>()).Argv);

    [Theory]
    [InlineData(OperationKind.FirewallAllow, "allow")]
    [InlineData(OperationKind.FirewallDeny, "deny")]
    [InlineData(OperationKind.FirewallDeleteRule, "delete")]
    public void ResolveHelperCommand_PortRule_DefaultsToTcpFromAnywhere(OperationKind kind, string subcommand)
    {
        var command = FirewallOperationExecutor.ResolveHelperCommand(kind, "8080", new Dictionary<string, string>());

        Assert.Null(command.Refusal);
        Assert.Equal(["-n", FirewallOperationExecutor.HelperPath, subcommand, "8080", "tcp", "any"], command.Argv);
    }

    [Fact]
    public void ResolveHelperCommand_PortRule_CarriesProtocolAndSource()
    {
        var command = FirewallOperationExecutor.ResolveHelperCommand(OperationKind.FirewallAllow, "53", new Dictionary<string, string>
        {
            [FirewallConstants.ProtoEnvVar] = "udp",
            [FirewallConstants.SourceEnvVar] = "10.0.0.0/8",
        });

        Assert.Equal(["-n", FirewallOperationExecutor.HelperPath, "allow", "53", "udp", "10.0.0.0/8"], command.Argv);
    }

    [Theory]
    [InlineData("icmp", "any", "Invalid protocol 'icmp' (allowed: tcp, udp)", -1)]
    [InlineData("tcp", "not-an-address", "Invalid source 'not-an-address' (expected a CIDR, an IP, or 'any')", -1)]
    public void ResolveHelperCommand_InvalidProtocolOrSource_IsRefused(string proto, string source, string refusal, int exitCode)
    {
        var command = FirewallOperationExecutor.ResolveHelperCommand(OperationKind.FirewallAllow, "8080", new Dictionary<string, string>
        {
            [FirewallConstants.ProtoEnvVar] = proto,
            [FirewallConstants.SourceEnvVar] = source,
        });

        Assert.Equal(refusal, command.Refusal);
        Assert.Equal(exitCode, command.ExitCode);
        Assert.Empty(command.Argv);
    }

    [Theory]
    [InlineData("22", null)]
    [InlineData("2222", "2222")]
    public void ResolveHelperCommand_ClosingAnAdministrationPort_IsRefused(string port, string? adminPort)
    {
        var env = new Dictionary<string, string>();
        if (adminPort is not null) env[FirewallConstants.AdminPortEnvVar] = adminPort;

        var command = FirewallOperationExecutor.ResolveHelperCommand(OperationKind.FirewallDeny, port, env);

        Assert.Equal($"Refused: this would close the administration port {port} and lock you out.", command.Refusal);
        Assert.Equal(1, command.ExitCode);
    }
}
