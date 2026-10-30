// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

/// <summary>R-119: TeamSpeak start/stop/restart were <c>systemctl ... teamspeak3</c> shell strings run without root. They
/// are typed operations now: the agent finds the installed unit (teamspeak-setup writes teamspeak3.service, a host
/// set up by hand may have teamspeak.service) and runs the argv-exact aetheus-teamspeak grant.</summary>
public sealed class TeamspeakServiceOperationExecutorTests
{
    private readonly List<string> _runs = [];
    private readonly List<(string Message, TaskLogLevel Level)> _output = [];

    private TeamspeakServiceOperationExecutor Executor(string[] installedUnits, int exitCode = 0, bool isLinux = true) =>
        new(Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 10, MaxTimeoutSeconds = 120 }),
            NullLogger<TeamspeakServiceOperationExecutor>.Instance)
        {
            IsLinux = isLinux,
            UnitExistsAsync = (unit, _) => Task.FromResult(installedUnits.Contains(unit)),
            RunProcessAsync = (startInfo, timeout, _, _, _) =>
            {
                _runs.Add($"{startInfo.FileName} {string.Join(' ', startInfo.ArgumentList)} timeout={timeout}");
                return Task.FromResult(new ExecutorResult(exitCode, false));
            }
        };

    private Task<ExecutorResult> RunAsync(TeamspeakServiceOperationExecutor executor, OperationKind kind, string target = "-") =>
        executor.ExecuteAsync(kind, target, 500,
            (message, level) => { _output.Add((message, level)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(OperationKind.TeamspeakStart, "start")]
    [InlineData(OperationKind.TeamspeakStop, "stop")]
    [InlineData(OperationKind.TeamspeakRestart, "restart")]
    public async Task ServiceOperation_RunsSystemctlOnTheUnitTeamspeakSetupInstalls(OperationKind kind, string verb)
    {
        var result = await RunAsync(Executor(["teamspeak3.service", "teamspeak.service"]), kind);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([$"sudo -n /bin/systemctl {verb} teamspeak3.service timeout=120"], _runs);
    }

    [Fact]
    public async Task ServiceOperation_FallsBackToTheTeamspeakUnit()
    {
        await RunAsync(Executor(["teamspeak.service"]), OperationKind.TeamspeakStop);

        Assert.Equal(["sudo -n /bin/systemctl stop teamspeak.service timeout=120"], _runs);
    }

    [Fact]
    public async Task ServiceOperation_WithoutAnyTeamspeakUnit_FailsAndSaysSo()
    {
        var result = await RunAsync(Executor([]), OperationKind.TeamspeakStart);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(_runs);
        Assert.Contains(_output, line => line.Level == TaskLogLevel.Error && line.Message.Contains("teamspeak3.service", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServiceOperation_ReportsTheSystemctlFailure()
    {
        var result = await RunAsync(Executor(["teamspeak3.service"], exitCode: 1), OperationKind.TeamspeakRestart);

        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task ServiceOperation_RefusesAnyOtherTarget_AndIsLinuxOnly()
    {
        Assert.NotEqual(0, (await RunAsync(Executor(["teamspeak3.service"]), OperationKind.TeamspeakStart, "ssh")).ExitCode);
        Assert.NotEqual(0, (await RunAsync(Executor(["teamspeak3.service"], isLinux: false), OperationKind.TeamspeakStart)).ExitCode);
        Assert.Empty(_runs);
    }

    [Fact]
    public void ServiceKinds_AreHandled_AndNeedTheTeamspeakControlCapability()
    {
        var executor = Executor([]);
        foreach (var kind in new[] { OperationKind.TeamspeakStart, OperationKind.TeamspeakStop, OperationKind.TeamspeakRestart })
        {
            Assert.True(executor.CanHandle(kind));
            Assert.Equal(AgentCapabilities.TeamspeakServiceControl, AgentCapabilities.RequiredFor(kind));
        }
        Assert.Contains(AgentCapabilities.TeamspeakServiceControl, AgentCapabilities.SoftwareCapabilities);
    }

    /// <summary>The aetheus-teamspeak drop-in grants every systemctl argv the agent can send (both units, the three verbs,
    /// which include the graceful restart's), in an alias that keeps noexec.</summary>
    [Fact]
    public void TheTeamspeakGrant_NamesEverySystemctlArgvTheAgentSends()
    {
        var dropIn = LinuxHostConfigTemplates.Read("teamspeak/sudoers.d/aetheus-teamspeak");
        foreach (var unit in TeamspeakUnit.Candidates)
        {
            foreach (var verb in new[] { "start", "stop", "restart" })
                Assert.Contains(string.Join(' ', TeamspeakUnit.BuildSystemctlArgv(verb, unit).Skip(1)), dropIn, StringComparison.Ordinal);
        }
        Assert.Contains("Defaults!AETHEUS_TEAMSPEAK_SERVICE noexec\n", dropIn, StringComparison.Ordinal);
        Assert.Contains("NOPASSWD: AETHEUS_TEAMSPEAK_SERVICE\n", dropIn, StringComparison.Ordinal);
        Assert.DoesNotContain("systemctl start *", dropIn, StringComparison.Ordinal);
    }
}
